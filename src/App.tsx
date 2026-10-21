import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { ArrowDown, ArrowUpRight, BookOpen, CalendarDays, Check, CheckCheck, ChevronDown, ChevronLeft, ChevronRight, Circle, Command, Download, FileText, FolderOpen, HelpCircle, ListTodo, LoaderCircle, Menu, Moon, PanelRight, Plus, Search, Sun, Upload, X, AlertCircle } from 'lucide-react';
import type { Block, Note, Vault } from './types';
import { extractLinks, indentBlock, isOutlineMarkdown, journalDate, journalPath, mergeBlock, moveBlock, newBlock, pagePath, parseMarkdown, serializeMarkdown, splitBlock, toggleTask, visibleBlocks } from './core';
import { desktop, discardDraftsAndReload, downloadFile, flushAll, openVault, recoveredDraft, scheduleSave, subscribeSave, type SaveState } from './storage';

const today = journalDate();
const taskPattern = /^(TODO|DOING|DONE|LATER|NOW|\[ \]|\[x\])\s*/i;
function Inline({ text, open }: { text: string; open: (title: string) => void }) {
  const parts = text.split(/(\[\[[^\]]+\]\]|#[\p{L}\p{N}_/-]+|\*\*[^*]+\*\*|`[^`]+`|https?:\/\/[^\s]+)/gu);
  return <>{parts.map((part, i) => part.startsWith('[[') ? <button key={i} className="inline-link" onClick={e => { e.stopPropagation(); open(part.slice(2, -2)); }}>{part.slice(2, -2)}</button> : /^#[\p{L}\p{N}]/u.test(part) ? <button key={i} className="tag" onClick={e => { e.stopPropagation(); open(part.slice(1)); }}>{part}</button> : part.startsWith('**') ? <strong key={i}>{part.slice(2, -2)}</strong> : part.startsWith('`') ? <code key={i}>{part.slice(1, -1)}</code> : /^https?:/.test(part) ? <a key={i} href={part} target="_blank" rel="noreferrer" onClick={e => e.stopPropagation()}>{part}</a> : <span key={i}>{part}</span>)}</>;
}
type EditorProps = { note: Note; onChange: (note: Note, content: string) => void; open: (title: string) => void; titles: string[] };
function Editor({ note, onChange, open, titles }: EditorProps) {
  const [blocks, setBlocks] = useState<Block[]>(() => parseMarkdown(note.content));
  const [active, setActive] = useState<string | null>(null);
  const [completion, setCompletion] = useState<{ id: string; start: number; end: number; type: string; query: string } | null>(null);
  const lastContent = useRef(note.content);
  const fields = useRef(new Map<string, HTMLTextAreaElement>());
  const pendingFocus = useRef<{ id: string; position?: number } | null>(null);
  const composing = useRef(false);
  useLayoutEffect(() => {
    const target = pendingFocus.current; if (!target) return;
    const field = fields.current.get(target.id); if (!field) return;
    field.focus(); const offset = target.position ?? field.value.length;
    field.setSelectionRange(offset, offset); pendingFocus.current = null;
  }, [blocks, active]);
  useEffect(() => {
    if (note.content !== lastContent.current) {
      const oldIndex = blocks.findIndex(block => block.id === active);
      const oldField = active ? fields.current.get(active) : undefined;
      const position = oldField?.selectionStart;
      const hadFocus = oldField === document.activeElement;
      const next = parseMarkdown(note.content);
      lastContent.current = note.content; setBlocks(next);
      if (hadFocus && oldIndex >= 0) focus(next[Math.min(oldIndex, next.length - 1)], position);
    }
  }, [note.content]);
  function commit(next: Block[]) { setBlocks(next); const content = serializeMarkdown(next, note.content); lastContent.current = content; onChange(note, content); }
  function focus(block: Block | undefined, position?: number) {
    if (!block) return;
    pendingFocus.current = { id: block.id, position }; setActive(block.id);
    const field = fields.current.get(block.id);
    if (field && document.activeElement === field) { const offset = position ?? field.value.length; field.setSelectionRange(offset, offset); pendingFocus.current = null; }
  }
  function resize(field: HTMLTextAreaElement) { field.style.height = '0px'; field.style.height = `${Math.max(30, field.scrollHeight)}px`; }
  function key(event: KeyboardEvent<HTMLTextAreaElement>, index: number) {
    if (composing.current || event.nativeEvent.isComposing || event.keyCode === 229) return;
    const field = event.currentTarget; const pos = field.selectionStart; const block = blocks[index];
    if (completion && event.key === 'Escape') { event.preventDefault(); setCompletion(null); return; }
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') { event.preventDefault(); commit(blocks.map((b, i) => i === index ? { ...b, text: toggleTask(b.text) } : b)); return; }
    if (event.key === 'Tab') { event.preventDefault(); commit(indentBlock(blocks, index, event.shiftKey)); focus(block, pos); }
    else if (event.altKey && ['ArrowUp', 'ArrowDown'].includes(event.key)) { event.preventDefault(); commit(moveBlock(blocks, index, event.key === 'ArrowUp' ? -1 : 1)); focus(block, pos); }
    else if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); const ids = new Set(blocks.map(b => b.id)); const source = field.selectionEnd > pos ? blocks.map((b, i) => i === index ? { ...b, text: b.text.slice(0, pos) + b.text.slice(field.selectionEnd) } : b) : blocks; const next = splitBlock(source, index, pos); commit(next); focus(next.find(b => !ids.has(b.id)) || next.find(b => b.id === block.id), 0); setCompletion(null); }
    else if (event.key === 'Backspace' && pos === 0 && field.selectionEnd === 0 && index > 0) { event.preventDefault(); const previous = blocks[index - 1]; const length = previous.text.length; commit(mergeBlock(blocks, index)); focus(previous, length); }
    else if (event.key === 'ArrowUp' && pos === 0) { const rows = visibleBlocks(blocks); const row = rows.findIndex(r => r.index === index); if (row > 0) { event.preventDefault(); focus(rows[row - 1].block); } }
    else if (event.key === 'ArrowDown' && pos === field.value.length) { const rows = visibleBlocks(blocks); const row = rows.findIndex(r => r.index === index); if (row < rows.length - 1) { event.preventDefault(); focus(rows[row + 1].block, 0); } }
  }
  const suggestions = completion ? titles.filter(t => t.toLowerCase().includes(completion.query.toLowerCase())).slice(0, 6) : [];
  if (!isOutlineMarkdown(note.content)) return <div className="raw-editor"><span className="quiet">此文件含复杂 Markdown，以原文模式保留格式</span><textarea aria-label={`${note.title} 原文`} value={note.content} onChange={e => onChange(note, e.target.value)} spellCheck={false} /></div>;
  return <div className="blocks">{visibleBlocks(blocks).map(({ block, index }) => {
    const hasChildren = blocks[index + 1]?.depth > block.depth;
    const task = block.text.match(taskPattern);
    const done = /^(DONE|\[x\])/i.test(block.text);
    const focused = active === block.id;
    return <div key={block.id} className={`block ${focused ? 'is-active' : ''} ${done ? 'done' : ''}`} style={{ marginLeft: `${block.depth * 24}px` }}>
      <button className={`block-bullet ${hasChildren ? 'has-children' : ''}`} aria-label={hasChildren ? (block.collapsed ? '展开子块' : '折叠子块') : '编辑块'} aria-expanded={hasChildren ? !block.collapsed : undefined} onClick={() => hasChildren ? commit(blocks.map(b => b.id === block.id ? { ...b, collapsed: !b.collapsed } : b)) : focus(block)}>{hasChildren ? (block.collapsed ? <ChevronRight size={13} /> : <ChevronDown size={13} />) : <span />}</button>
      {task && <button aria-label="切换任务状态" className={`task-check ${done ? 'checked' : ''}`} onClick={() => commit(blocks.map(b => b.id === block.id ? { ...b, text: toggleTask(b.text) } : b))}>{done ? <Check size={13} /> : /^DOING|^NOW/.test(block.text) ? <Circle size={10} /> : null}</button>}
      <div className="block-body">
        {focused ? <textarea ref={el => { if (el) { fields.current.set(block.id, el); resize(el); } else fields.current.delete(block.id); }} aria-label="记录块" value={block.text} rows={1} spellCheck={false} onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }} onBlur={() => { setTimeout(() => setActive(current => current === block.id ? null : current), 120); }} onKeyDown={e => key(e, index)} onPaste={e => {
          if (composing.current) return;
          const text = e.clipboardData.getData('text/plain'); if (!text.includes('\n')) return;
          e.preventDefault(); const field = e.currentTarget;
          const lines = text.replace(/\r\n/g, '\n').split('\n');
          const first = { ...block, text: block.text.slice(0, field.selectionStart) + lines[0] };
          const inserted = lines.slice(1).map(line => newBlock(line, block.depth));
          const last = inserted[inserted.length - 1]; const offset = last.text.length; last.text += block.text.slice(field.selectionEnd);
          let end = index + 1; while (end < blocks.length && blocks[end].depth > block.depth) end++;
          commit([...blocks.slice(0, index), first, ...blocks.slice(index + 1, end), ...inserted, ...blocks.slice(end)]); focus(last, offset);
        }} onChange={e => {
          const text = e.target.value; commit(blocks.map(b => b.id === block.id ? { ...b, text } : b)); resize(e.target);
          const cursor = e.target.selectionStart; const match = text.slice(0, cursor).match(/(\[\[|#)([^\]\s]*)$/);
          setCompletion(match ? { id: block.id, start: cursor - match[0].length, end: cursor, type: match[1], query: match[2] } : null);
        }} /> : <div className="block-preview" tabIndex={0} onFocus={e => { if (e.target === e.currentTarget) focus(block); }} onClick={e => { if (!(e.target as HTMLElement).closest('button,a')) focus(block); }}>{block.text ? <Inline text={task ? block.text.replace(taskPattern, '') : block.text.replace(/^#{1,6}\s/, '')} open={open} /> : <span className="placeholder">记录今天的第一件事… Enter 换条，Tab 缩进</span>}</div>}
        {completion?.id === block.id && suggestions.length > 0 && <div className="completion">{suggestions.map(title => <button key={title} onMouseDown={e => { e.preventDefault(); const insert = completion.type === '[[' ? `[[${title}]]` : `#${title}`; const text = block.text.slice(0, completion.start) + insert + block.text.slice(completion.end); commit(blocks.map(b => b.id === block.id ? { ...b, text } : b)); setCompletion(null); focus(block, completion.start + insert.length); }}><FileText size={14} />{title}</button>)}</div>}
      </div>
    </div>;
  })}<button className="add-block" onClick={() => { const block = newBlock(); commit([...blocks, block]); focus(block, 0); }}><Plus size={14} /> 添加一条记录</button></div>;
}

export default function App() {
  const [vault, setVault] = useState<Vault | null>(null);
  const vaultRef = useRef<Vault | null>(null);
  const [view, setView] = useState<'journals' | 'pages' | 'tasks'>('journals');
  const [selected, setSelected] = useState<string | null>(null);
  const [limit, setLimit] = useState(7);
  const [search, setSearch] = useState<string | null>(null);
  const [panel, setPanel] = useState(true);
  const [sidebar, setSidebar] = useState(false);
  const [help, setHelp] = useState(false);
  const [dark, setDark] = useState(() => localStorage.getItem('flowlog.theme') === 'dark');
  const [states, setStates] = useState<Record<string, SaveState>>({});
  const statesRef = useRef(states); statesRef.current = states;
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [newTitle, setNewTitle] = useState<string | null>(null);
  const [jumpDate, setJumpDate] = useState(today);
  const history = useRef<{ past: { path: string; content: string }[]; future: { path: string; content: string }[]; last: string; time: number }>({ past: [], future: [], last: '', time: 0 });
  const importRef = useRef<HTMLInputElement>(null);
  const taskCache = useRef(new WeakMap<Note, { block: Block; index: number }[]>());
  function applyVault(next: Vault) { vaultRef.current = next; setVault(next); }
  const changeNote = useCallback((note: Note, content: string, record = true) => {
    const current = vaultRef.current; if (!current) return;
    const existing = current.notes.find(n => n.path === note.path) || note;
    if (existing.content === content && current.notes.some(n => n.path === note.path)) return;
    if (record) {
      const h = history.current; const now = Date.now();
      if (h.last !== note.path || now - h.time > 700) h.past.push({ path: note.path, content: existing.content });
      h.future = []; h.last = note.path; h.time = now;
    }
    const updated = { ...existing, content }; const notes = current.notes.some(n => n.path === note.path) ? current.notes.map(n => n.path === note.path ? updated : n) : [...current.notes, updated];
    const next = { ...current, notes }; vaultRef.current = next; setVault(next); scheduleSave(updated);
  }, []);
  async function load(choose = false) {
    setError('');
    try {
      await flushAll();
      setStates({});
      const next = await openVault(choose);
      let recovered = false;
      next.notes = next.notes.map(note => { const draft = recoveredDraft(note); if (draft !== null && draft !== note.content) { recovered = true; return { ...note, content: draft }; } return note; });
      if (!next.notes.some(n => n.path === journalPath(today))) next.notes.push({ path: journalPath(today), title: today, date: today, kind: 'journal', content: '', revision: '' });
      applyVault(next); setSelected(null); setView('journals'); history.current = { past: [], future: [], last: '', time: 0 };
      if (recovered) setNotice('已恢复未保存草稿。确认内容后编辑即可再次保存；也可先导出备份。');
    } catch (reason) { setError(String(reason)); }
  }
  useEffect(() => { void load(); return subscribeSave((path, state) => setStates(old => ({ ...old, [path]: state }))); }, []);
  useEffect(() => { document.documentElement.dataset.theme = dark ? 'dark' : 'light'; localStorage.setItem('flowlog.theme', dark ? 'dark' : 'light'); }, [dark]);
  useEffect(() => {
    function handler(event: globalThis.KeyboardEvent) {
      if (event.isComposing) return;
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); setSearch(current => current === null ? '' : null); }
      if (event.key === 'Escape') { setSearch(null); setHelp(false); setNewTitle(null); }
      if ((event.ctrlKey || event.metaKey) && (event.key.toLowerCase() === 'z' || event.key.toLowerCase() === 'y')) {
        if (!(event.target instanceof HTMLTextAreaElement)) return;
        event.preventDefault(); const h = history.current; const redo = event.shiftKey || event.key.toLowerCase() === 'y'; const source = redo ? h.future : h.past; const target = redo ? h.past : h.future; const entry = source.pop();
        if (entry) { const note = vaultRef.current?.notes.find(n => n.path === entry.path); if (note) { target.push({ path: note.path, content: note.content }); changeNote(note, entry.content, false); h.last = ''; } }
      }
    }
    window.addEventListener('keydown', handler); return () => window.removeEventListener('keydown', handler);
  }, [changeNote]);
  useEffect(() => {
    let disposed = false; let unlisten: (() => void) | undefined;
    if (desktop) void import('@tauri-apps/api/window').then(async ({ getCurrentWindow }) => {
      const win = getCurrentWindow(); const stop = await win.onCloseRequested(async event => { event.preventDefault(); try { await flushAll(); await win.destroy(); } catch (reason) { setError(`关闭前保存失败，窗口已保留：${String(reason)}`); } });
      if (disposed) stop(); else unlisten = stop;
    });
    const beforeUnload = (event: BeforeUnloadEvent) => { if (Object.values(statesRef.current).some(s => s.status !== 'saved')) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', beforeUnload);
    return () => { disposed = true; unlisten?.(); window.removeEventListener('beforeunload', beforeUnload); };
  }, []);
  function openPage(title: string) {
    if (!title.trim()) return;
    const current = vaultRef.current; if (!current) return;
    let note = current.notes.find(n => n.title.toLowerCase() === title.trim().toLowerCase());
    if (!note) { note = { path: pagePath(title.trim()), title: title.trim(), kind: 'page', content: '', revision: '' }; applyVault({ ...current, notes: [...current.notes, note] }); scheduleSave(note); }
    setSelected(note.path); setView(note.kind === 'journal' ? 'journals' : 'pages'); setSearch(null); setNewTitle(null); setSidebar(false); window.scrollTo(0, 0);
  }
  function goJournal(date: string) {
    const current = vaultRef.current; if (!current || !/^\d{4}-\d{2}-\d{2}$/.test(date)) return;
    const path = journalPath(date); if (!current.notes.some(n => n.path === path)) { const note: Note = { path, title: date, date, kind: 'journal', content: '', revision: '' }; applyVault({ ...current, notes: [...current.notes, note] }); }
    setView('journals'); setSelected(date === today ? null : path); setJumpDate(date);
  }
  function exportNotes() { downloadFile(`FlowLog-备份-${today}.json`, JSON.stringify({ version: 1, notes: vaultRef.current?.notes || [] }, null, 2)); }
  async function importNotes(file: File | undefined) {
    if (!file) return;
    try {
      const data = JSON.parse(await file.text()); if (!Array.isArray(data.notes)) throw new Error('备份文件缺少 notes');
      for (const item of data.notes) {
        if (typeof item.content !== 'string' || typeof item.title !== 'string' || !['journal', 'page'].includes(item.kind)) throw new Error('备份格式无效');
      }
      for (const item of data.notes) { const title = item.title.trim(); const date = /^\d{4}-\d{2}-\d{2}$/.test(item.date || title) ? (item.date || title) : undefined; if (!title || (item.kind === 'journal' && !date)) continue;
        const path = item.kind === 'journal' ? journalPath(date!) : pagePath(title); const exists = vaultRef.current?.notes.find(n => n.path === path);
        if (exists) { const copy = { ...exists, path: pagePath(`${title}（导入 ${Date.now()}）`), title: `${title}（导入副本）`, kind: 'page' as const, revision: '' }; const current = vaultRef.current!; applyVault({ ...current, notes: [...current.notes, copy] }); changeNote(copy, item.content); }
        else { const note: Note = { path, title, kind: item.kind, date, content: '', revision: '' }; changeNote(note, item.content); }
      }
      await flushAll(); setNotice('备份已导入，重名笔记保留为副本。');
    } catch (reason) { setError(`导入失败：${String(reason)}`); }
  }
  const notes = vault?.notes || [];
  const journals = notes.filter(n => n.kind === 'journal').sort((a, b) => (a.date || a.title) === today ? -1 : (b.date || b.title) === today ? 1 : (b.date || b.title).localeCompare(a.date || a.title));
  const pages = notes.filter(n => n.kind === 'page').sort((a, b) => a.title.localeCompare(b.title, 'zh-CN'));
  const selectedNote = notes.find(n => n.path === selected);
  const shown = selectedNote ? [selectedNote] : journals.slice(0, limit);
  const backlinks = selectedNote ? notes.filter(n => n.path !== selectedNote.path && extractLinks(n.content).some(title => title.toLowerCase() === selectedNote.title.toLowerCase())) : [];
  const searchResults = search === null ? [] : notes.filter(n => !search || `${n.title}\n${n.content}`.toLowerCase().includes(search.toLowerCase())).slice(0, 30);
  const allStates = Object.values(states); const issue = allStates.find(s => s.status === 'conflict' || s.status === 'error'); const saving = allStates.some(s => s.status === 'saving');
  const taskNotes = notes.flatMap(note => {
    let tasks = taskCache.current.get(note);
    if (!tasks) {
      tasks = isOutlineMarkdown(note.content) ? parseMarkdown(note.content).map((block, index) => ({ block, index })).filter(({ block }) => taskPattern.test(block.text)) : [];
      taskCache.current.set(note, tasks);
    }
    return tasks.map(task => ({ note, ...task }));
  });
  const weekday = (date: string) => { const d = new Date(`${date}T12:00:00`); return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString('zh-CN', { weekday: 'long' }); };
  return <div className="app-shell">
    <aside className={`sidebar ${sidebar ? 'mobile-open' : ''}`}>
      <a className="brand" href="#" onClick={e => { e.preventDefault(); setSelected(null); setView('journals'); }}><span className="brand-symbol"><BookOpen size={21} /></span><div>FlowLog<span>流记 · 让思绪自然发生</span></div></a>
      <button className="search-trigger" onClick={() => setSearch('')}><Search size={16} /><span>搜索笔记</span><kbd>⌃ K</kbd></button>
      <div className="nav-label">工作空间</div>
      <nav>{([{ id: 'journals', label: '每日笔记', icon: CalendarDays, count: journals.length }, { id: 'pages', label: '全部页面', icon: FileText, count: pages.length }, { id: 'tasks', label: '任务清单', icon: ListTodo, count: taskNotes.filter(t => !/^DONE|^\[x\]/i.test(t.block.text)).length }] as const).map(item => <button key={item.id} className={view === item.id ? 'active' : ''} onClick={() => { setView(item.id); setSelected(null); setSidebar(false); }}><item.icon size={18} />{item.label}<span className="nav-count">{item.count}</span></button>)}</nav>
      <div className="nav-label pages-label">页面 <button aria-label="新建页面" onClick={() => setNewTitle('')}><Plus size={15} /></button></div>
      <div className="page-nav">{pages.length ? pages.slice(0, 9).map(note => <button className={selected === note.path ? 'selected' : ''} key={note.path} onClick={() => openPage(note.title)}><span className="page-dot" />{note.title}</button>) : <p>用 [[页面名称]] 串联想法</p>}</div>
      <div className="sidebar-bottom"><div className="local-badge"><span />{desktop ? '本地优先 · 数据在你手中' : '浏览器演示 · 数据仅存此浏览器'}</div><button className="vault-button" onClick={() => void load(true)} title={vault?.root}><FolderOpen size={19} /><div><strong>{desktop ? '我的笔记库' : '浏览器演示'}</strong><small>{vault?.root || '正在打开…'}</small></div><ChevronDown size={14} /></button><div className="sidebar-tools"><button title="快捷键帮助" onClick={() => setHelp(true)}><HelpCircle size={17} /><span>使用帮助</span></button><button title={dark ? '切换浅色主题' : '切换深色主题'} aria-label="切换主题" onClick={() => setDark(!dark)}>{dark ? <Sun size={17} /> : <Moon size={17} />}</button></div></div>
    </aside>
    <div className="workspace">
      <header className="topbar"><div className="breadcrumb"><button className="mobile-menu icon-button" aria-label="展开导航" onClick={() => setSidebar(!sidebar)}><Menu size={18} /></button><BookOpen size={15} /><span>我的空间</span><ChevronRight size={13} /><strong>{selectedNote?.title || (view === 'journals' ? '每日笔记' : view === 'pages' ? '全部页面' : '任务清单')}</strong></div><div className="top-actions"><button className={`save-status ${issue ? 'save-error' : ''}`} onClick={() => { if (issue) void flushAll().catch(e => setError(String(e))); }} title={issue?.message}>{issue ? <AlertCircle size={14} /> : saving ? <LoaderCircle size={14} className="spin" /> : <CheckCheck size={14} />}{issue ? '保存失败 · 查看 / 重试' : saving ? '正在保存…' : desktop ? '已保存到本地' : '已保存在浏览器'}</button><span className="top-divider" /><button className="icon-button" aria-label="显示右侧面板" onClick={() => setPanel(!panel)}><PanelRight size={17} /></button></div></header>
      {error && <div className="banner error"><AlertCircle size={16} /><span>{error}</span><button onClick={exportNotes}>导出备份</button><button aria-label="关闭提示" onClick={() => setError('')}><X size={15} /></button></div>}
      {notice && <div className="banner"><Check size={16} /><span>{notice}</span><button aria-label="关闭提示" onClick={() => setNotice('')}><X size={15} /></button></div>}
      {issue && <div className="banner error"><AlertCircle size={16} /><span>{issue.status === 'conflict' ? '检测到外部修改，已停止覆盖。请导出当前草稿，再重新读取磁盘版本。' : `保存失败，草稿暂存于此设备：${issue.message}`}</span><button onClick={exportNotes}>导出草稿</button>{issue.status === 'conflict' && <button onClick={() => { if (window.confirm('请先导出草稿。继续将放弃此笔记库未保存的更改，并重新读取磁盘文件。')) void discardDraftsAndReload().then(next => { applyVault(next); setStates({}); setSelected(null); setNotice('已放弃未保存草稿，重新读取磁盘文件。'); }).catch(reason => setError(String(reason))); }}>放弃草稿并重新读取</button>}</div>}
      <div className={`content-layout ${!panel ? 'panel-closed' : ''}`}><main className="main-content">
        {view === 'journals' && <><div className="view-heading"><div className="eyebrow">YOUR EVERYDAY, CONNECTED</div><div className="heading-line"><h1>{selectedNote ? '每日笔记' : '每一天，都值得记录'}</h1><button className="subtle-button" onClick={() => goJournal(today)}><CalendarDays size={15} />回到今天</button></div><p>捕捉灵感，梳理日常。让零散的想法，慢慢连成线。</p></div>{shown.map(note => <section className="journal-entry" key={note.path}><div className="date-heading"><div className="date-icon"><CalendarDays size={19} /></div><h2>{(note.date || note.title).replace(/-/g, '.')}</h2><span className="weekday">{weekday(note.date || note.title)}</span>{(note.date || note.title) === today && <span className="today-badge">今天</span>}<button aria-label="查看此日期" className="entry-link" onClick={() => { setSelected(note.path); setPanel(true); }}><ArrowUpRight size={17} /></button></div><Editor note={note} onChange={changeNote} open={openPage} titles={pages.map(p => p.title)} /><div className="entry-footer"><span>{parseMarkdown(note.content).filter(b => b.text.trim()).length} 条记录</span><span>{states[note.path]?.status === 'saving' ? '保存中…' : states[note.path]?.status === 'conflict' ? '存在冲突' : '安静记录，自然生长'}</span></div></section>)}{!selectedNote && journals.length > limit && <button className="load-more" onClick={() => setLimit(limit + 7)}><ArrowDown size={15} />加载更早的日记</button>}{notes.filter(n => n.content.trim()).length === 0 && <div className="welcome-card"><span className="welcome-spark">✦</span><h3>从一条小小的记录开始</h3><p>写下此刻的想法，按 Enter 继续下一条。<br />用 [[双向链接]] 把相关的事情连起来。</p><button onClick={() => { const note = notes.find(n => n.path === journalPath(today)); if (note) { changeNote(note, '- 今天，给思绪留一点空间。\n- TODO 整理本周的计划\n- 一个小想法，关于 [[我的灵感]] #生活\n  - 不必完整，先记录下来。'); } }}>试试示例记录 <ArrowUpRight size={14} /></button></div>}</>}
        {view === 'pages' && <>{selectedNote ? <><div className="view-heading"><button className="back-button" onClick={() => setSelected(null)}><ChevronLeft size={15} />全部页面</button><h1>{selectedNote.title}</h1><p>在这里，让一个想法慢慢展开。</p></div><Editor note={selectedNote} onChange={changeNote} open={openPage} titles={pages.map(p => p.title)} /></> : <><div className="view-heading"><div className="eyebrow">A PLACE FOR EVERY THOUGHT</div><div className="heading-line"><h1>全部页面</h1><button className="primary-button" onClick={() => setNewTitle('')}><Plus size={15} />新建页面</button></div><p>把零散的记录，组织成自己的知识。</p></div><div className="pages-list">{pages.map(note => <button key={note.path} onClick={() => openPage(note.title)}><span className="file-icon"><FileText size={20} /></span><div><strong>{note.title}</strong><p>{note.content.replace(/^[-#\s]+/g, '').slice(0, 75) || '一张白纸，等待你的想法'}</p></div><ChevronRight size={16} /></button>)}</div>{!pages.length && <div className="empty-state"><FileText size={32} /><h3>为一个想法，留一张专属页面</h3><p>在日记中输入 [[页面名称]]，或直接新建页面。</p><button className="primary-button" onClick={() => setNewTitle('')}>新建第一个页面</button></div>}</>}</>}
        {view === 'tasks' && <><div className="view-heading"><div className="eyebrow">ONE THING AT A TIME</div><h1>任务清单</h1><p>{taskNotes.filter(t => !/^DONE|^\[x\]/i.test(t.block.text)).length} 项待办，给每件事一点前进的空间。</p></div><div className="tasks-list">{taskNotes.map(({ note, block, index }) => <div key={`${note.path}:${index}`} className={/^DONE|^\[x\]/i.test(block.text) ? 'task-row done' : 'task-row'}><button className="task-check" aria-label="切换任务状态" onClick={() => { const blocks = parseMarkdown(note.content); blocks[index] = { ...blocks[index], text: toggleTask(block.text) }; changeNote(note, serializeMarkdown(blocks, note.content)); }}>{/^DONE|^\[x\]/i.test(block.text) ? <Check size={13} /> : null}</button><div><Inline text={block.text.replace(taskPattern, '')} open={openPage} /><button className="task-source" onClick={() => { setSelected(note.path); setView(note.kind === 'journal' ? 'journals' : 'pages'); }}>{note.title} <ArrowUpRight size={11} /></button></div></div>)}</div>{!taskNotes.length && <div className="empty-state"><CheckCheck size={34} /><h3>给下一步行动一个位置</h3><p>在记录中输入 TODO，或按 Ctrl + Enter 创建任务。</p></div>}</>}
        <footer className="main-footer"><span className="footer-dot" />留一点空白，让思绪流动。</footer>
      </main>{panel && <aside className="right-panel"><div className="right-title">{selectedNote ? '关联与参考' : '日记导航'}<button className="icon-button" aria-label="关闭右侧面板" onClick={() => setPanel(false)}><X size={14} /></button></div><div className="mini-calendar"><div className="calendar-month"><CalendarDays size={15} />{new Date(`${jumpDate}T12:00:00`).toLocaleDateString('zh-CN', { year: 'numeric', month: 'long' })}</div><div className="calendar-week">{'一二三四五六日'.split('').map(d => <span key={d}>{d}</span>)}</div><div className="calendar-grid">{(() => { const base = new Date(`${jumpDate}T12:00:00`); const start = new Date(base.getFullYear(), base.getMonth(), 1); const offset = (start.getDay() + 6) % 7; const days = new Date(base.getFullYear(), base.getMonth() + 1, 0).getDate(); return [...Array(offset + days)].map((_, i) => { const day = i - offset + 1; if (day < 1) return <span key={i} />; const date = `${base.getFullYear()}-${String(base.getMonth() + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`; return <button key={i} className={`${date === today ? 'calendar-today' : ''} ${journals.some(n => n.date === date && n.content.trim()) ? 'has-note' : ''}`} onClick={() => goJournal(date)}>{day}</button>; }); })()}</div><label className="date-jump">跳转日期<input aria-label="跳转日期" type="date" value={jumpDate} onChange={e => { if (e.target.value) goJournal(e.target.value); }} /></label></div><div className="right-section"><div className="section-label">{selectedNote ? `双向链接 · ${backlinks.length}` : '记录小提示'}</div>{selectedNote ? backlinks.length ? backlinks.map(note => <button className="backlink" key={note.path} onClick={() => { setSelected(note.path); setView(note.kind === 'journal' ? 'journals' : 'pages'); }}><FileText size={14} /><div>{note.title}<p>{note.content.split('\n').find(line => extractLinks(line).includes(selectedNote.title))?.replace(/^\s*-\s*/, '').slice(0, 90)}</p></div></button>) : <p className="right-copy">其他页面提及 [[{selectedNote.title}]] 时，会出现在这里。</p> : <><p className="right-copy">不用想好写什么。<br />从眼前的一件小事开始。</p><div className="tip-line"><kbd>Enter</kbd><span>开始下一条记录</span></div><div className="tip-line"><kbd>Tab</kbd><span>让想法多一层</span></div><div className="tip-line"><kbd>[[ ]]</kbd><span>连接相关的页面</span></div><button className="help-link" onClick={() => setHelp(true)}>查看全部快捷键 <ArrowUpRight size={13} /></button></>}</div><div className="right-section data-tools"><div className="section-label">笔记与备份</div><button onClick={exportNotes}><Download size={15} />导出全部笔记 JSON</button><button onClick={() => importRef.current?.click()}><Upload size={15} />导入 JSON 备份</button><small>{desktop ? 'Markdown 原文保存在笔记库文件夹。' : '演示数据仅存当前浏览器，请导出备份。'}</small><input hidden ref={importRef} type="file" accept=".json" onChange={e => { void importNotes(e.target.files?.[0]); e.target.value = ''; }} /></div></aside>}</div>
    </div>
    {search !== null && <div className="modal-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) setSearch(null); }}><div className="search-modal" role="dialog" aria-modal="true" aria-label="搜索笔记"><div className="search-input"><Search size={20} /><input autoFocus value={search} onChange={e => setSearch(e.target.value)} placeholder="搜索页面、日记和灵感…" /><button aria-label="关闭搜索" onClick={() => setSearch(null)}><kbd>Esc</kbd></button></div><div className="search-results">{searchResults.length ? searchResults.map(note => <button key={note.path} onClick={() => { setSelected(note.path); setView(note.kind === 'journal' ? 'journals' : 'pages'); setSearch(null); }}><FileText size={17} /><div><strong>{note.title}</strong><p>{note.content.replace(/^[-#\s]+/g, '').slice(0, 110) || '空白笔记'}</p></div><ArrowUpRight size={15} /></button>) : <div className="search-empty">没有找到相关记录。<button onClick={() => openPage(search)}>创建「{search}」页面</button></div>}</div><div className="search-footer">搜索当前笔记库的全部内容 <span>点击打开 · Esc 关闭</span></div></div></div>}
    {newTitle !== null && <div className="modal-backdrop"><form className="small-modal" onSubmit={e => { e.preventDefault(); openPage(newTitle); }}><button type="button" className="modal-close" aria-label="关闭" onClick={() => setNewTitle(null)}><X size={18} /></button><FileText size={27} /><h2>给想法一个名字</h2><p>页面可以随时通过 [[双向链接]] 找到。</p><input autoFocus aria-label="页面名称" placeholder="例如：阅读笔记" value={newTitle} onChange={e => setNewTitle(e.target.value)} maxLength={120} /><button className="primary-button" type="submit" disabled={!newTitle.trim()}>创建页面</button></form></div>}
    {help && <div className="modal-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) setHelp(false); }}><div className="help-modal" role="dialog" aria-modal="true" aria-label="使用帮助"><button className="modal-close" aria-label="关闭帮助" onClick={() => setHelp(false)}><X size={18} /></button><Command size={26} /><h2>让记录跟得上思绪</h2><p>所有笔记都属于你，随时可以导出。</p>{[['下一条记录', 'Enter'], ['块内换行', 'Shift + Enter'], ['增加 / 减少层级', 'Tab / Shift + Tab'], ['移动整棵子树', 'Alt + ↑ / ↓'], ['切换 TODO → DOING → DONE', 'Ctrl + Enter'], ['搜索笔记', 'Ctrl + K'], ['撤销 / 重做', 'Ctrl + Z / Ctrl + Shift + Z'], ['跨块导航', '块首 ↑ / 块尾 ↓'], ['连接页面 / 添加标签', '[[页面]] / #标签']].map(([label, keys]) => <div className="shortcut" key={label}><span>{label}</span><kbd>{keys}</kbd></div>)}<div className="help-note">支持 **加粗**、`行内代码`、链接与多级大纲。包含表格、代码围栏等复杂结构的 Markdown 会以原文模式打开，保留原始格式。{!desktop && ' 当前为浏览器演示，无法直接读写磁盘文件。'}</div></div></div>}
  </div>;
}
