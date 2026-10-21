import { invoke, isTauri } from '@tauri-apps/api/core';
import type { Note, Vault } from './types';

export const desktop = isTauri();
export type SaveState = { status: 'saved' | 'saving' | 'error' | 'conflict'; message?: string };
const DEMO_KEY = 'flowlog.demo.v1';
let root = '';
const pending = new Map<string, Note>();
const revisions = new Map<string, string>();
const running = new Map<string, Promise<void>>();
const timers = new Map<string, ReturnType<typeof setTimeout>>();
const conflicts = new Set<string>();
const listeners = new Set<(path: string, state: SaveState) => void>();
const draftKey = (path: string) => `flowlog.draft:${root}:${path}`;
function notify(path: string, state: SaveState) { listeners.forEach(fn => fn(path, state)); }
export function subscribeSave(fn: (path: string, state: SaveState) => void) { listeners.add(fn); return () => { listeners.delete(fn); }; }
function demo(): Vault { try { return JSON.parse(localStorage.getItem(DEMO_KEY) || 'null') || { root: '浏览器演示', notes: [] }; } catch { return { root: '浏览器演示', notes: [] }; } }
function register(vault: Vault): Vault {
  root = vault.root; revisions.clear(); conflicts.clear(); pending.clear();
  vault.notes.forEach(note => revisions.set(note.path, note.revision));
  const prefix = `flowlog.draft:${root}:`;
  for (let index = 0; index < localStorage.length; index++) {
    const key = localStorage.key(index);
    if (!key?.startsWith(prefix)) continue;
    try { const draft = JSON.parse(localStorage.getItem(key) || 'null') as Note | null;
      if (draft && typeof draft.path === 'string' && typeof draft.content === 'string') {
        const disk = vault.notes.find(n => n.path === draft.path);
        if (disk?.content === draft.content) { localStorage.removeItem(key); index--; continue; }
        if ((disk && disk.revision !== draft.revision) || (!disk && !!draft.revision)) conflicts.add(draft.path);
        if (!disk) vault.notes.push(draft);
        scheduleSave(draft);
      }
    } catch { /* Legacy text drafts belong to existing notes. */ }
  }
  return vault;
}
export async function openVault(choose = false): Promise<Vault> {
  await flushAll();
  const vault = desktop ? await invoke<Vault>(choose ? 'choose_vault' : 'open_default_vault') : demo();
  if (!vault) throw new Error('未选择笔记库');
  return register(vault);
}
export function recoveredDraft(note: Note): string | null {
  const value = localStorage.getItem(draftKey(note.path));
  if (value === null) return null;
  try { const draft = JSON.parse(value); return typeof draft?.content === 'string' ? draft.content : value; } catch { return value; }
}
export async function discardDraftsAndReload(): Promise<Vault> {
  await Promise.allSettled([...running.values()]);
  timers.forEach(clearTimeout); timers.clear(); pending.clear(); conflicts.clear();
  const prefix = `flowlog.draft:${root}:`;
  for (let index = localStorage.length - 1; index >= 0; index--) { const key = localStorage.key(index); if (key?.startsWith(prefix)) localStorage.removeItem(key); }
  const vault = desktop ? await invoke<Vault>('list_vault') : demo();
  return register(vault);
}
export function scheduleSave(note: Note) {
  pending.set(note.path, { ...note });
  try {
    let revision = revisions.get(note.path) || note.revision;
    if (conflicts.has(note.path)) {
      try { revision = JSON.parse(localStorage.getItem(draftKey(note.path)) || 'null')?.revision ?? note.revision; } catch { revision = note.revision; }
    }
    localStorage.setItem(draftKey(note.path), JSON.stringify({ ...note, revision }));
  } catch { /* The live editor still retains content. */ }
  if (conflicts.has(note.path)) { notify(note.path, { status: 'conflict', message: '磁盘文件已改变。草稿已保留，请下载草稿后重新打开笔记库。' }); return; }
  notify(note.path, { status: 'saving' });
  clearTimeout(timers.get(note.path));
  timers.set(note.path, setTimeout(() => { void flushNote(note.path).catch(() => {}); }, 350));
}
export async function flushNote(path: string): Promise<void> {
  clearTimeout(timers.get(path)); timers.delete(path);
  const active = running.get(path);
  if (active) { await active; if (pending.has(path)) return flushNote(path); return; }
  if (conflicts.has(path)) throw new Error('外部修改冲突：请先保留草稿并重新打开笔记库');
  const work = (async () => {
    while (pending.has(path)) {
      const note = pending.get(path)!; pending.delete(path);
      try {
        if (desktop) {
          const result = await invoke<{ revision: string }>('save_note', { path, content: note.content, expectedRevision: revisions.get(path) || note.revision || '' });
          revisions.set(path, result.revision);
          const next = pending.get(path);
          if (next) localStorage.setItem(draftKey(path), JSON.stringify({ ...next, revision: result.revision }));
        } else {
          const vault = demo(); const index = vault.notes.findIndex(n => n.path === path);
          const saved = { ...note, revision: String(Date.now()) };
          if (index < 0) vault.notes.push(saved); else vault.notes[index] = saved;
          localStorage.setItem(DEMO_KEY, JSON.stringify(vault)); revisions.set(path, saved.revision);
        }
        if (!pending.has(path)) { localStorage.removeItem(draftKey(path)); notify(path, { status: 'saved' }); }
      } catch (error) {
        if (!pending.has(path)) pending.set(path, note);
        const message = String(error);
        const conflict = /conflict|modified|冲突|已修改|已改变/i.test(message);
        if (conflict) conflicts.add(path);
        notify(path, { status: conflict ? 'conflict' : 'error', message });
        throw error;
      }
    }
  })();
  running.set(path, work);
  try { await work; } finally { running.delete(path); }
}
export async function flushAll() {
  const paths = new Set([...pending.keys(), ...running.keys()]);
  await Promise.all([...paths].map(flushNote));
}
export function downloadFile(name: string, content: string, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
