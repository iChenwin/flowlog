import type { Block } from './types';

export function newBlock(text = '', depth = 0): Block {
  return { id: crypto.randomUUID(), text, depth: Math.max(0, depth) };
}

function indentation(value: string): number {
  return value.replace(/\t/g, '  ').length / 2;
}

/** Only the explicitly supported outline subset is editable as blocks. */
export function isOutlineMarkdown(content: string): boolean {
  if (!content.trim()) return true;
  let foundBlock = false;
  let fenced = false;
  for (const line of content.replace(/\r\n/g, '\n').split('\n')) {
    if (!line.trim()) continue;
    if (/^\s*```/.test(line)) fenced = !fenced;
    if (/^([\t ]*)- (.*)$/.test(line) && !fenced) {
      const prefix = /^([\t ]*)/.exec(line)![1];
      if (!Number.isInteger(indentation(prefix))) return false;
      foundBlock = true;
    } else if (!foundBlock || !/^(\t| {2})/.test(line)) return false;
  }
  return foundBlock;
}

export function parseMarkdown(content: string): Block[] {
  if (!content.trim()) return [newBlock()];
  const blocks: Block[] = [];
  let fenced = false;
  const lines = content.replace(/\r\n/g, '\n').split('\n');
  if (lines.at(-1) === '') lines.pop();
  for (const line of lines) {
    const match = /^([\t ]*)- (.*)$/.exec(line);
    if (match && !fenced) {
      const block = newBlock(match[2], Math.floor(indentation(match[1])));
      blocks.push(block);
      if (/^```/.test(match[2])) fenced = true;
    } else if (blocks.length) {
      const block = blocks.at(-1)!;
      // Remove only the continuation prefix; unknown syntax remains literal text.
      const prefix = '\t'.repeat(block.depth + 1);
      const spaces = ' '.repeat((block.depth + 1) * 2);
      const text = line.startsWith(prefix) ? line.slice(prefix.length) : line.startsWith(spaces) ? line.slice(spaces.length) : line;
      block.text += '\n' + text;
      if (/^\s*```/.test(text)) fenced = !fenced;
    } else blocks.push(newBlock(line));
  }
  return blocks.length ? blocks : [newBlock()];
}

export function serializeMarkdown(blocks: Block[], original = ''): string {
  const eol = original.includes('\r\n') ? '\r\n' : '\n';
  return blocks.map(block => {
    const [first, ...rest] = block.text.replace(/\r\n/g, '\n').split('\n');
    return '\t'.repeat(block.depth) + '- ' + first + rest.map(line => eol + '\t'.repeat(block.depth + 1) + line).join('');
  }).join(eol) + eol;
}
export const parseBlocks = parseMarkdown;
export const serializeBlocks = serializeMarkdown;

function subtreeEnd(blocks: Block[], index: number): number {
  let end = index + 1;
  while (end < blocks.length && blocks[end].depth > blocks[index].depth) end++;
  return end;
}

export function splitBlock(blocks: Block[], index: number, offset: number): Block[] {
  const current = blocks[index];
  if (!current) return blocks;
  if (!current.text && current.depth > 0) return indentBlock(blocks, index, true);
  const at = Math.max(0, Math.min(offset, current.text.length));
  const tail = newBlock(current.text.slice(at), current.depth);
  const copy = blocks.map(block => ({ ...block }));
  copy[index].text = current.text.slice(0, at);
  copy[index].collapsed = false;
  // A sibling is inserted after existing descendants to preserve their parent.
  copy.splice(subtreeEnd(blocks, index), 0, tail);
  return copy;
}

export function indentBlock(blocks: Block[], index: number, outdent = false): Block[] {
  const current = blocks[index];
  if (!current) return blocks;
  const delta = outdent ? -1 : 1;
  if (outdent && current.depth === 0) return blocks;
  if (!outdent) {
    if (index === 0) return blocks;
    // Must have a preceding sibling under which the subtree can nest.
    let previous = index - 1;
    while (previous >= 0 && blocks[previous].depth > current.depth) previous--;
    if (previous < 0 || blocks[previous].depth !== current.depth) return blocks;
  }
  const end = subtreeEnd(blocks, index);
  return blocks.map((block, i) => i >= index && i < end ? { ...block, depth: block.depth + delta } : block);
}

export function moveBlock(blocks: Block[], index: number, direction: -1 | 1): Block[] {
  const current = blocks[index];
  if (!current) return blocks;
  const end = subtreeEnd(blocks, index);
  if (direction === -1) {
    let previous = index - 1;
    while (previous >= 0 && blocks[previous].depth > current.depth) previous--;
    if (previous < 0 || blocks[previous].depth !== current.depth) return blocks;
    return [...blocks.slice(0, previous), ...blocks.slice(index, end), ...blocks.slice(previous, index), ...blocks.slice(end)];
  }
  if (end >= blocks.length || blocks[end].depth !== current.depth) return blocks;
  const nextEnd = subtreeEnd(blocks, end);
  return [...blocks.slice(0, index), ...blocks.slice(end, nextEnd), ...blocks.slice(index, end), ...blocks.slice(nextEnd)];
}

export function mergeBlock(blocks: Block[], index: number): Block[] {
  if (index <= 0 || index >= blocks.length) return blocks;
  const previous = blocks[index - 1];
  const current = blocks[index];
  const copy = blocks.map(block => ({ ...block }));
  copy[index - 1].text = previous.text + current.text;
  copy[index - 1].collapsed = false;
  copy.splice(index, 1);
  // Descendants now belong to the surviving block.
  const end = subtreeEnd(blocks, index);
  const depthDelta = previous.depth - current.depth;
  for (let i = index; i < end - 1; i++) copy[i].depth = Math.max(previous.depth + 1, copy[i].depth + depthDelta);
  return copy;
}

export function visibleBlocks(blocks: Block[]): { block: Block; index: number }[] {
  const result: { block: Block; index: number }[] = [];
  let hiddenBelow: number | undefined;
  blocks.forEach((block, index) => {
    if (hiddenBelow !== undefined && block.depth > hiddenBelow) return;
    hiddenBelow = block.collapsed ? block.depth : undefined;
    result.push({ block, index });
  });
  return result;
}

export function toggleTask(text: string): string {
  if (/^TODO\b/.test(text)) return text.replace(/^TODO\b/, 'DOING');
  if (/^DOING\b/.test(text)) return text.replace(/^DOING\b/, 'DONE');
  if (/^DONE\b/.test(text)) return text.replace(/^DONE\b/, 'TODO');
  if (/^\[ \]/.test(text)) return text.replace(/^\[ \]/, '[x]');
  if (/^\[x\]/i.test(text)) return text.replace(/^\[x\]/i, '[ ]');
  return 'TODO ' + text;
}

export function extractLinks(text: string): string[] {
  const links = new Set<string>();
  for (const match of text.matchAll(/\[\[([^\]\n]+)\]\]|(?:^|\s)#([\p{L}\p{N}_\-/]+)/gu)) links.add((match[1] ?? match[2]).trim());
  return [...links].filter(Boolean);
}

export function journalDate(date = new Date()): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}
export function journalPath(date: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new Error('无效日期');
  const parsed = new Date(date + 'T12:00:00');
  if (Number.isNaN(parsed.valueOf()) || journalDate(parsed) !== date) throw new Error('无效日期');
  return 'journals/' + date.replace(/-/g, '_') + '.md';
}

/** Encoded segments prevent titles becoming filesystem paths. */
export function pagePath(title: string): string {
  const normalized = title.trim().normalize('NFC');
  if (!normalized) throw new Error('页面名称不能为空');
  let name = encodeURIComponent(normalized).replace(/[!'()*]/g, ch => '%' + ch.charCodeAt(0).toString(16).toUpperCase());
  if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name)) name = '%' + name.charCodeAt(0).toString(16).toUpperCase() + name.slice(1);
  name = name.replace(/\.$/, '%2E');
  if (name.length > 220) throw new Error('页面名称过长，请缩短名称');
  return 'pages/' + name + '.md';
}
export function titleFromPath(path: string): string {
  const base = path.split('/').at(-1)!.replace(/\.md$/i, '');
  if (path.startsWith('journals/')) return base.replace(/_/g, '-');
  try { return decodeURIComponent(base); } catch { return base; }
}
