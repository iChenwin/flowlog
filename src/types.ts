export interface Block { id: string; text: string; depth: number; collapsed?: boolean }
export interface Note { path: string; title: string; kind: 'journal' | 'page'; content: string; revision: string; date?: string }
export interface Vault { root: string; notes: Note[] }
export interface SaveResult { revision: string }
