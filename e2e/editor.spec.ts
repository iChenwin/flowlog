import { expect, test, type Page } from '@playwright/test';
import { journalDate, journalPath, pagePath } from '../src/core';
import type { Note } from '../src/types';
const today = journalDate();
const journal = (content: string, date = today): Note => ({ path: journalPath(date), date, title: date, kind: 'journal', content, revision: 'r0' });
async function seed(page: Page, notes: Note[] = []) {
  await page.addInitScript(notes => { if (!localStorage.getItem('flowlog.demo.v1')) localStorage.setItem('flowlog.demo.v1', JSON.stringify({ root: '浏览器演示', notes })); }, notes);
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
}
test('Chinese daily editing, nesting, tasks and reload preserve content', async ({ page }) => {
  await seed(page);
  await page.locator('.block-preview').first().click();
  const editor = page.getByRole('textbox', { name: '记录块', exact: true });
  await editor.fill('今天完成设计 📝'); await editor.press('Enter');
  await expect(editor).toBeFocused(); await editor.fill('安排评审'); await editor.press('Tab');
  await expect(page.locator('.block.is-active')).toHaveCSS('margin-left', '24px');
  await editor.press('Control+Enter'); await expect(editor).toHaveValue('TODO 安排评审');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('flowlog.demo.v1'))).toContain('TODO 安排评审');
  await page.reload();
  await expect(page.locator('.journal-entry')).toContainText('今天完成设计');
  await expect(page.locator('.journal-entry')).toContainText('安排评审');
  await expect(page.getByRole('button', { name: '切换任务状态' })).toHaveCount(1);
});
test('IME confirmation does not split a block', async ({ page }) => {
  await seed(page); await page.locator('.block-preview').first().click();
  const editor = page.getByRole('textbox', { name: '记录块', exact: true });
  await editor.dispatchEvent('compositionstart'); await editor.fill('中文候选'); await editor.press('Enter');
  await expect(page.locator('.block')).toHaveCount(1);
  await editor.dispatchEvent('compositionend'); await editor.press('Enter');
  await expect(page.locator('.block')).toHaveCount(2);
});
test('page links create pages and backlinks return to the source', async ({ page }) => {
  await seed(page, [journal('- 关于 [[项目甲]] 的想法\n')]);
  await page.getByRole('button', { name: '项目甲', exact: true }).click();
  await expect(page.getByRole('heading', { name: '项目甲', level: 1 })).toBeVisible();
  await expect(page.locator('.right-panel')).toContainText('双向链接 · 1');
  await page.locator('.backlink').click(); await expect(page.locator('.journal-entry')).toContainText('关于');
  await page.keyboard.press('Control+k'); await page.getByPlaceholder('搜索页面、日记和灵感…').fill('项目甲');
  await expect(page.locator('.search-results')).toContainText('关于');
});
test('daily stream loads older dates and date navigation returns to today', async ({ page }) => {
  const notes = Array.from({ length: 12 }, (_, index) => { const date = new Date(); date.setDate(date.getDate() - index); return journal('- 第 ' + index + ' 天的记录\n', journalDate(date)); });
  await seed(page, notes); await expect(page.locator('.journal-entry')).toHaveCount(7);
  await page.getByRole('button', { name: '加载更早的日记' }).click(); await expect(page.locator('.journal-entry')).toHaveCount(12);
  await page.getByLabel('跳转日期').fill(notes[10].date!); await expect(page.locator('.journal-entry')).toHaveCount(1);
  await page.getByRole('button', { name: '回到今天' }).click(); await expect(page.locator('.journal-entry')).toHaveCount(12);
});
test('complex Markdown stays in raw mode', async ({ page }) => {
  const content = '# 原始标题\n\n| 列 | 值 |\n| -- | -- |\n| A | B |\n';
  const note: Note = { path: pagePath('原文'), title: '原文', kind: 'page', content, revision: 'r0' };
  await seed(page, [note]); await page.getByRole('button', { name: '原文', exact: true }).click();
  await expect(page.getByLabel('原文 原文')).toHaveValue(content);
  await page.getByLabel('原文 原文').fill(content + '\n追加中文');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('flowlog.demo.v1'))).toContain('追加中文');
});
test('Markdown HTML is rendered as text without executing', async ({ page }) => {
  await seed(page, [journal('- <img src=x onerror="window.injected=true">\n')]);
  await expect(page.locator('.block-preview')).toContainText('<img');
  expect(await page.evaluate(() => (window as unknown as { injected?: boolean }).injected)).toBeUndefined();
  await expect(page.locator('.block-preview img')).toHaveCount(0);
});
test('structural undo and redo keep the editor usable', async ({ page }) => {
  await seed(page, [journal('- 第一条\n- 第二条\n')]);
  await page.locator('.block-preview').nth(1).click();
  const editor = page.getByRole('textbox', { name: '记录块', exact: true });
  await editor.press('Tab'); await expect(page.locator('.block').nth(1)).toHaveCSS('margin-left', '24px');
  await editor.press('Control+z'); await expect(page.locator('.block').nth(1)).toHaveCSS('margin-left', '0px');
  await expect(editor).toBeFocused();
  await editor.press('Control+Shift+z'); await expect(page.locator('.block').nth(1)).toHaveCSS('margin-left', '24px');
});
test('multiline paste keeps existing children attached to their parent', async ({ page }) => {
  await seed(page, [journal('- 父块\n\t- 原子块\n- 下一项\n')]);
  await page.locator('.block-preview').first().click();
  const editor = page.getByRole('textbox', { name: '记录块', exact: true });
  await editor.press('End');
  await editor.evaluate(field => { const data = new DataTransfer(); data.setData('text/plain', '新增一\n新增二'); field.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true })); });
  await expect(page.locator('.block')).toHaveCount(4);
  await expect(page.locator('.block').nth(1)).toContainText('原子块');
  await expect(page.locator('.block').nth(1)).toHaveCSS('margin-left', '24px');
  await expect(editor).toHaveValue('新增二');
  await page.getByRole('button', { name: '折叠子块' }).click();
  await expect(page.locator('.block')).toHaveCount(3);
  await expect(page.locator('.blocks')).not.toContainText('原子块');
});
test('task overview does not rewrite complex source documents', async ({ page }) => {
  const raw: Note = { path: pagePath('混合文档'), title: '混合文档', kind: 'page', content: '# 标题\n- TODO 保留格式\n', revision: 'r0' };
  await seed(page, [raw]);
  await page.getByRole('button', { name: /任务清单/ }).click();
  await expect(page.locator('.tasks-list .task-row')).toHaveCount(0);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('flowlog.demo.v1')!).notes[0].content)).toBe(raw.content);
});
test('crash recovery reveals a new page that was never saved', async ({ page }) => {
  const fresh: Note = { path: pagePath('恢复笔记'), title: '恢复笔记', kind: 'page', content: '- 重启后找回的中文草稿\n', revision: '' };
  await page.addInitScript(note => { localStorage.setItem('flowlog.draft:浏览器演示:' + note.path, JSON.stringify(note)); }, fresh);
  await seed(page);
  await page.getByRole('button', { name: '恢复笔记', exact: true }).click();
  await expect(page.locator('.blocks')).toContainText('重启后找回的中文草稿');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('flowlog.demo.v1'))).toContain('重启后找回的中文草稿');
});
test('conflict recovery controls remain available after a failed vault change', async ({ page }) => {
  const disk = journal('- 外部编辑的新版本\n');
  const draft = { ...disk, content: '- 旧基线的本地草稿\n', revision: 'older' };
  await page.addInitScript(note => { localStorage.setItem('flowlog.draft:浏览器演示:' + note.path, JSON.stringify(note)); }, draft);
  await seed(page, [disk]);
  await expect(page.getByRole('button', { name: '放弃草稿并重新读取' })).toBeVisible();
  await page.locator('.vault-button').click();
  await expect(page.getByRole('button', { name: '放弃草稿并重新读取' })).toBeVisible();
  await expect(page.locator('.banner.error').first()).toContainText('冲突');
});
