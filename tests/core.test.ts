import { describe, expect, it } from 'vitest';
import { extractLinks, indentBlock, isOutlineMarkdown, journalDate, journalPath, mergeBlock, moveBlock, newBlock, pagePath, parseMarkdown, serializeMarkdown, splitBlock, titleFromPath, toggleTask, visibleBlocks } from '../src/core';

describe('Markdown subset and preservation boundaries', () => {
  it('round trips Chinese nested tasks and multiline blocks', () => {
    const source = '- 今天记录 📝\n\t- TODO [[项目A]]\n\t\t补充说明\n- #工作 完成\n';
    expect(serializeMarkdown(parseMarkdown(source))).toBe(source);
  });
  it('keeps CRLF style', () => {
    const source = '- 中文\r\n\t- 子块\r\n';
    expect(serializeMarkdown(parseMarkdown(source), source)).toBe(source);
  });
  it('keeps fenced code list markers inside the same block', () => {
    const source = '- ```js\n\t- literal\n\t```\n- next\n';
    const blocks = parseMarkdown(source);
    expect(blocks).toHaveLength(2);
    expect(blocks[0].text).toBe('```js\n- literal\n```');
    expect(serializeMarkdown(blocks)).toBe(source);
  });
  it('requires raw editing for general Markdown rather than silently converting it', () => {
    expect(isOutlineMarkdown('# 标题\n正文\n- 列表\n')).toBe(false);
    expect(isOutlineMarkdown('title:: 页面\n- 内容\n')).toBe(false);
    expect(isOutlineMarkdown('- 父块\n\t- 子块\n')).toBe(true);
    expect(isOutlineMarkdown('')).toBe(true);
  });
  it('preserves unknown properties and IDs as literal continuation text', () => {
    const source = '- parent\n\tid:: abc-123\n\tcustom:: value\n';
    expect(serializeMarkdown(parseMarkdown(source))).toBe(source);
  });
});

describe('subtree editing', () => {
  const tree = () => [newBlock('A'), newBlock('a', 1), newBlock('B'), newBlock('b', 1), newBlock('C')];
  it('moves an entire subtree without detaching children', () => {
    expect(moveBlock(tree(), 2, -1).map(x => x.text)).toEqual(['B', 'b', 'A', 'a', 'C']);
    expect(moveBlock(tree(), 0, 1).map(x => x.text)).toEqual(['B', 'b', 'A', 'a', 'C']);
  });
  it('indents and outdents an entire subtree', () => {
    expect(indentBlock(tree(), 2).map(x => x.depth)).toEqual([0, 1, 1, 2, 0]);
    expect(indentBlock(indentBlock(tree(), 2), 2, true).map(x => x.depth)).toEqual([0, 1, 0, 1, 0]);
    expect(indentBlock(tree(), 0).map(x => x.depth)).toEqual([0, 1, 0, 1, 0]);
  });
  it('splits at the cursor and preserves ID and descendants', () => {
    const original = tree();
    const result = splitBlock(original, 0, 1);
    expect(result[0].id).toBe(original[0].id);
    expect(result.map(x => x.text)).toEqual(['A', 'a', '', 'B', 'b', 'C']);
    expect(original).toHaveLength(5);
  });
  it('outdents an empty nested block on Enter', () => {
    const result = splitBlock([newBlock('parent'), newBlock('', 1)], 1, 0);
    expect(result).toHaveLength(2);
    expect(result[1].depth).toBe(0);
  });
  it('merges into the previous block while preserving children', () => {
    const result = mergeBlock([newBlock('first'), newBlock('second'), newBlock('child', 1)], 1);
    expect(result.map(x => [x.text, x.depth])).toEqual([['firstsecond', 0], ['child', 1]]);
  });
  it('hides all descendants of collapsed parents', () => {
    const blocks = tree(); blocks[0].collapsed = true;
    expect(visibleBlocks(blocks).map(x => x.index)).toEqual([0, 2, 3, 4]);
  });
  it('cycles supported task syntaxes', () => {
    expect(toggleTask('事项')).toBe('TODO 事项');
    expect(toggleTask('TODO 事项')).toBe('DOING 事项');
    expect(toggleTask('DOING 事项')).toBe('DONE 事项');
    expect(toggleTask('DONE 事项')).toBe('TODO 事项');
    expect(toggleTask('[ ] task')).toBe('[x] task');
    expect(toggleTask('[x] task')).toBe('[ ] task');
  });
});

describe('links and safe file names', () => {
  it('finds page refs and Unicode tags without duplicates', () => {
    expect(extractLinks('[[项目 A]] #工作 [[项目 A]] #工作')).toEqual(['项目 A', '工作']);
  });
  it('uses local dates and rejects invalid journal dates', () => {
    expect(journalDate(new Date(2026, 9, 21, 0, 1))).toBe('2026-10-21');
    expect(journalPath('2026-10-21')).toBe('journals/2026_10_21.md');
    expect(() => journalPath('2026-02-31')).toThrow();
    expect(() => journalPath('../secret')).toThrow();
  });
  it('encodes filesystem separators and reserved Windows names', () => {
    for (const title of ['项目 A', '../private', 'CON', 'LPT1', 'hello/world', 'a:b', 'tail.']) {
      const path = pagePath(title);
      expect(path.slice(6)).not.toMatch(/[\\/:*?"<>|]/);
      expect(titleFromPath(path)).toBe(title);
    }
    expect(() => pagePath('')).toThrow();
  });
});
