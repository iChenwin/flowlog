import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Note } from '../src/types';

const bridge = vi.hoisted(() => ({ invoke: vi.fn(), isTauri: vi.fn(() => true) }));
vi.mock('@tauri-apps/api/core', () => bridge);
class MemoryStorage {
  data = new Map<string, string>();
  get length() { return this.data.size; }
  key(index: number) { return [...this.data.keys()][index] ?? null; }
  getItem(key: string) { return this.data.get(key) ?? null; }
  setItem(key: string, value: string) { this.data.set(key, String(value)); }
  removeItem(key: string) { this.data.delete(key); }
  clear() { this.data.clear(); }
}
const note: Note = { path: 'journals/2026_10_21.md', title: '2026-10-21', date: '2026-10-21', kind: 'journal', content: '- 原稿\n', revision: 'r0' };

beforeEach(() => {
  vi.resetModules(); vi.useFakeTimers(); bridge.invoke.mockReset(); bridge.isTauri.mockReturnValue(true);
  vi.stubGlobal('localStorage', new MemoryStorage());
  bridge.invoke.mockImplementation(async (command: string) => {
    if (command === 'open_default_vault' || command === 'list_vault' || command === 'choose_vault') return { root: 'D:/Notes', notes: [{ ...note }] };
    return { revision: 'r1' };
  });
});
afterEach(() => { vi.clearAllTimers(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('serial automatic saving', () => {
  it('uses the latest acknowledged revision for edits made during a save', async () => {
    const storage = await import('../src/storage');
    await storage.openVault();
    let release!: (value: { revision: string }) => void;
    bridge.invoke.mockImplementationOnce(() => new Promise(resolve => { release = resolve; }));
    storage.scheduleSave({ ...note, content: '- 第一稿\n' });
    const saving = storage.flushNote(note.path);
    storage.scheduleSave({ ...note, content: '- 第二稿\n' });
    release({ revision: 'r1' });
    await saving;
    const saves = bridge.invoke.mock.calls.filter(call => call[0] === 'save_note');
    expect(saves).toHaveLength(2);
    expect(saves[0][1]).toMatchObject({ content: '- 第一稿\n', expectedRevision: 'r0' });
    expect(saves[1][1]).toMatchObject({ content: '- 第二稿\n', expectedRevision: 'r1' });
    expect(storage.recoveredDraft(note)).toBeNull();
  });
  it('preserves a draft and reports failure until an explicit retry succeeds', async () => {
    const storage = await import('../src/storage'); await storage.openVault();
    const states: string[] = []; storage.subscribeSave((_path, state) => states.push(state.status));
    bridge.invoke.mockRejectedValueOnce(new Error('Disk full'));
    storage.scheduleSave({ ...note, content: '- 不能丢失\n' });
    await expect(storage.flushAll()).rejects.toThrow('Disk full');
    expect(storage.recoveredDraft(note)).toBe('- 不能丢失\n');
    expect(states.at(-1)).toBe('error');
    await storage.flushAll();
    expect(states.at(-1)).toBe('saved');
    expect(storage.recoveredDraft(note)).toBeNull();
  });
  it('blocks overwriting after a version conflict', async () => {
    const storage = await import('../src/storage'); await storage.openVault();
    bridge.invoke.mockRejectedValueOnce(new Error('conflict: external edit'));
    storage.scheduleSave({ ...note, content: '- 本地改动\n' });
    await expect(storage.flushNote(note.path)).rejects.toThrow(/conflict/);
    const count = bridge.invoke.mock.calls.length;
    storage.scheduleSave({ ...note, content: '- 最新本地改动\n' });
    await expect(storage.flushNote(note.path)).rejects.toThrow();
    expect(bridge.invoke.mock.calls).toHaveLength(count);
    expect(storage.recoveredDraft(note)).toBe('- 最新本地改动\n');
  });
  it('flushes pending content before changing vaults', async () => {
    const storage = await import('../src/storage'); await storage.openVault();
    storage.scheduleSave({ ...note, content: '- 退出前最后一句\n' });
    await storage.openVault(true);
    const commands = bridge.invoke.mock.calls.map(call => call[0]);
    expect(commands.indexOf('save_note')).toBeLessThan(commands.indexOf('choose_vault'));
  });
});
