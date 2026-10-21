import { chromium } from '@playwright/test';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { cpus, totalmem } from 'node:os';

const installed = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const browser = await chromium.launch({ headless: true, ...(existsSync(installed) ? { executablePath: installed } : {}) });
try {
  const page = await browser.newPage();
  const notes = Array.from({ length: 365 }, (_, i) => {
    const d = new Date(); d.setDate(d.getDate() - i);
    const date = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    const count = i < 145 ? 28 : 27;
    return { path: 'journals/' + date.replaceAll('-', '_') + '.md', date, title: date, kind: 'journal', revision: 'benchmark',
      content: Array.from({ length: count }, (_, j) => '- ' + (j % 7 === 0 ? 'TODO ' : '') + `第${i}天，第${j}条中文记录 [[测试页面]] #测试`).join('\n') + '\n' };
  });
  await page.addInitScript(notes => localStorage.setItem('flowlog.demo.v1', JSON.stringify({ root: '浏览器演示', notes })), notes);
  const started = performance.now();
  await page.goto('http://127.0.0.1:1420');
  await page.getByRole('heading', { level: 1 }).waitFor();
  const startupMs = Math.round(performance.now() - started);
  await page.locator('.block-preview').first().click();
  const field = page.getByRole('textbox', { name: '记录块', exact: true });
  await field.press('End');
  const samples = [];
  for (let i = 0; i < 15; i++) { const at = performance.now(); await field.press('a'); samples.push(performance.now() - at); }
  await page.context().setOffline(true);
  await field.press('x');
  await page.waitForFunction(() => JSON.parse(localStorage.getItem('flowlog.demo.v1')).notes[0].content.includes('aaaaaaaaaaaaaaax'));
  samples.sort((a, b) => a - b);
  const report = { environment: 'Headless Chrome, Vite development browser demo; includes automation overhead, not native desktop measurement',
    cpu: cpus()[0]?.model, memoryGiB: Math.round(totalmem() / 1024 ** 3), browser: browser.version(),
    days: 365, blocks: 10000, startupMs, keypressWallMedianMs: Math.round(samples[7]), keypressWallMaxMs: Math.round(samples[14]),
    offlineBrowserSave: true, renderedBlocks: await page.locator('.block').count() };
  mkdirSync('artifacts', { recursive: true });
  writeFileSync('artifacts/performance-browser.json', JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
} finally { await browser.close(); }
