import { chromium, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

const [phase, vaultArg, journalArg] = process.argv.slice(2);
if (process.env.GITHUB_ACTIONS !== 'true' || !process.env.RUNNER_TEMP) throw new Error('Native smoke is CI-only.');
if (!['write', 'reopen-conflict'].includes(phase)) throw new Error('Expected write or reopen-conflict phase.');
const vault = path.resolve(vaultArg);
const journal = path.resolve(journalArg);
const under = (parent, child) => { const relative = path.relative(parent, child); return !!relative && !relative.startsWith('..') && !path.isAbsolute(relative); };
if (!under(path.resolve(process.env.RUNNER_TEMP), vault) || !under(vault, journal)) throw new Error('Smoke vault must remain inside RUNNER_TEMP.');
const saved = '真实桌面中文记录：今天完成本地保存验证';
const draft = '发生外部冲突后仍保留的中文草稿';
const external = '- 外部编辑器写入：此内容不可被覆盖\n';
const reportPath = path.resolve('artifacts/native-smoke.json');
let page;
let browser;
let report = {};
try {
  try { report = JSON.parse(await fs.readFile(reportPath, 'utf8')); } catch { /* First phase. */ }
  const deadline = Date.now() + 45000;
  while (!browser && Date.now() < deadline) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9222', { timeout: 3000 }); }
    catch { await new Promise(resolve => setTimeout(resolve, 500)); }
  }
  if (!browser) throw new Error('WebView2 CDP did not become available within 45 seconds.');
  await expect.poll(() => browser.contexts().flatMap(context => context.pages()).length, { timeout: 15000 }).toBeGreaterThan(0);
  page = browser.contexts().flatMap(context => context.pages())[0];
  page.on('console', message => { if (message.type() === 'error') console.error('[WebView2 console]', message.text()); });
  page.on('pageerror', error => console.error('[WebView2 pageerror]', error));
  await expect(page.locator('h1')).toBeVisible({ timeout: 30000 });
  await expect(page.getByRole('button', { name: '已保存到本地', exact: true })).toBeVisible();
  // Rust canonical paths may have a Windows extended-length prefix.
  const selectedVault = await page.locator('.vault-button').getAttribute('title');
  expect(path.resolve(selectedVault.replace(/^\\\\\?\\/, '')).toLowerCase()).toBe(vault.toLowerCase());
  const preview = page.locator('.journal-entry .block-preview').first();
  if (phase === 'write') {
    await expect(preview).toContainText('原生桥接测试初始记录');
    await preview.click();
    await page.getByRole('textbox', { name: '记录块', exact: true }).fill(saved);
    await expect.poll(async () => fs.readFile(journal, 'utf8'), { timeout: 15000 }).toBe(`- ${saved}\n`);
    await expect(page.getByRole('button', { name: '已保存到本地', exact: true })).toBeVisible();
    console.log('PASS: Chinese text crossed the real Tauri bridge and reached the Markdown file.');
  } else {
    await expect(preview).toContainText(saved);
    await fs.writeFile(journal, external, 'utf8');
    await preview.click();
    await page.getByRole('textbox', { name: '记录块', exact: true }).fill(draft);
    await expect(page.getByText('检测到外部修改，已停止覆盖。请导出当前草稿，再重新读取磁盘版本。', { exact: true })).toBeVisible({ timeout: 15000 });
    await expect.poll(async () => {
      const directory = path.join(vault, '.flowlog', 'conflicts');
      try { const files = await fs.readdir(directory); return (await Promise.all(files.map(file => fs.readFile(path.join(directory, file), 'utf8')))).some(text => text.includes(draft)); } catch { return false; }
    }, { timeout: 15000 }).toBe(true);
    expect(await fs.readFile(journal, 'utf8')).toBe(external);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: '放弃草稿并重新读取', exact: true }).click();
    await expect(page.locator('.journal-entry .block-preview').first()).toContainText('外部编辑器写入：此内容不可被覆盖');
    expect(await fs.readFile(journal, 'utf8')).toBe(external);
    await expect(page.getByRole('button', { name: '已保存到本地', exact: true })).toBeVisible();
    console.log('PASS: Relaunch loaded saved contents; disk and conflicting draft both survived; explicit discard cleared pending state.');
  }
  await fs.mkdir(path.dirname(reportPath), { recursive: true });
  await page.screenshot({ path: 'artifacts/native-smoke.png', fullPage: true });
  const closed = page.waitForEvent('close', { timeout: 15000 });
  // Use the same close command as Tauri's Window.close(). A hidden window may
  // not have a .NET MainWindowHandle; this still exercises closeRequested.
  await page.evaluate(() => { setTimeout(() => { void window.__TAURI_INTERNALS__.invoke('plugin:window|close', { label: 'main' }); }, 50); });
  await closed;
  report[phase] = { passed: true, normalWindowClose: true, timestamp: new Date().toISOString(), transport: 'installed Tauri + WebView2 CDP', journal };
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2));
  // Do not browser.close(): the app owns WebView2. Exiting disconnects CDP,
  // PowerShell verifies that the normal close above also exits the process.
  process.exit(0);
} catch (error) {
  console.error(`FAIL: native smoke ${phase}`, error);
  await fs.mkdir(path.dirname(reportPath), { recursive: true });
  if (page) await page.screenshot({ path: 'artifacts/native-smoke.png', fullPage: true }).catch(screenshotError => console.error('Screenshot failed:', screenshotError));
  report[phase] = { passed: false, timestamp: new Date().toISOString(), error: String(error) };
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2));
  process.exit(1);
}
