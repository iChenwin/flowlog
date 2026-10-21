using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FlowLog.Native
{
    public sealed class MainWindow : Window
    {
        sealed class EditState
        {
            public Note Note;
            public OutlineEditor Editor;
            public string Pending;
            public bool Busy, Failed, Conflict;
            public string Error;
        }
        VaultStore store;
        List<Note> notes = new List<Note>();
        readonly Dictionary<string, EditState> states = new Dictionary<string, EditState>(StringComparer.OrdinalIgnoreCase);
        readonly StackPanel stream = new StackPanel();
        readonly StackPanel side = new StackPanel();
        readonly StackPanel related = new StackPanel();
        readonly TextBox search = new TextBox();
        readonly TextBlock status = new TextBlock();
        readonly TextBlock heading = new TextBlock();
        readonly TextBlock vaultLabel = new TextBlock();
        readonly ScrollViewer scroll = new ScrollViewer();
        readonly DispatcherTimer saveTimer = new DispatcherTimer();
        readonly DispatcherTimer searchTimer = new DispatcherTimer();
        readonly DispatcherTimer closeTimer = new DispatcherTimer();
        Grid layout;
        Border sidebar, rightbar, topbar;
        Brush ink, muted, paper, panel, edge, accent;
        string view = "journals";
        Note active;
        int journalCount = 7;
        bool dark, sourceMode, closingRequested, closeAllowed, loading;

        public MainWindow(string rootOverride = null)
        {
            Title = "流记 · FlowLog";
            Width = 1250; Height = 850; MinWidth = 880; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AutomationProperties.SetAutomationId(this, "MainWindow");
            SetPalette();
            BuildShell();
            saveTimer.Interval = TimeSpan.FromMilliseconds(350);
            saveTimer.Tick += delegate { saveTimer.Stop(); FlushAll(); RefreshRelated(); };
            searchTimer.Interval = TimeSpan.FromMilliseconds(220);
            searchTimer.Tick += delegate { searchTimer.Stop(); if (search.Text.Trim().Length > 0) ShowSearch(); else ShowJournals(); };
            closeTimer.Interval = TimeSpan.FromMilliseconds(100);
            closeTimer.Tick += delegate { TryFinishClose(); };
            Closing += OnClosing;
            Closed += delegate { saveTimer.Stop(); searchTimer.Stop(); closeTimer.Stop(); };
            PreviewKeyDown += OnWindowKey;
            string root = rootOverride;
            if (String.IsNullOrWhiteSpace(root)) root = VaultStore.LoadLastRoot();
            if (!String.IsNullOrWhiteSpace(root) && Directory.Exists(root)) OpenVault(root);
            else ShowWelcome();
        }

        void SetPalette()
        {
            ink = B(dark ? "#E5E8EB" : "#26343B"); muted = B(dark ? "#A1AEB7" : "#72818B");
            paper = B(dark ? "#20262B" : "#FFFFFF"); panel = B(dark ? "#192025" : "#F4F6F7");
            edge = B(dark ? "#39444C" : "#E6EBEE"); accent = B(dark ? "#6DD4C3" : "#117E74");
            Background = paper; Foreground = ink;
        }
        static Brush B(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        Button Button(string label, Action action)
        {
            Button b = new Button { Content = label, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(12, 7, 12, 7), HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Foreground = ink, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            b.Click += delegate { action(); }; return b;
        }
        TextBlock Label(string value, double size, Brush color)
        { return new TextBlock { Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap }; }
        void BuildShell()
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
            layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
            layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
            Content = layout;
            sidebar = new Border { Background = panel, BorderBrush = edge, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16, 22, 16, 12), Child = side };
            Grid.SetRowSpan(sidebar, 3); layout.Children.Add(sidebar);
            side.Children.Add(Label("流记", 25, ink));
            side.Children.Add(new TextBlock { Text = "FLOWLOG", FontSize = 10, Foreground = muted, Margin = new Thickness(2, 2, 0, 30) });
            side.Children.Add(Button("◷   每日笔记", ShowJournals));
            side.Children.Add(Button("▤   全部页面", ShowPages));
            side.Children.Add(Button("☑   待办任务", ShowTasks));
            side.Children.Add(new Border { Height = 1, Background = edge, Margin = new Thickness(0, 18, 0, 15) });
            side.Children.Add(Button("＋   新建页面", CreatePage));
            side.Children.Add(Button("⌂   选择笔记库", ChooseVault));
            vaultLabel.Foreground = muted; vaultLabel.FontSize = 11; vaultLabel.TextWrapping = TextWrapping.Wrap; vaultLabel.Margin = new Thickness(12, 6, 4, 22); side.Children.Add(vaultLabel);
            side.Children.Add(Button("◐   切换明暗", ToggleTheme));
            side.Children.Add(Button("?   快捷键与说明", ShowHelp));
            StackPanel top = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            heading.Text = "每日笔记"; heading.FontSize = 18; heading.FontWeight = FontWeights.SemiBold; heading.Foreground = ink; heading.Margin = new Thickness(0, 0, 26, 0); heading.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(heading);
            search.Width = 220; search.Padding = new Thickness(10, 6, 10, 6); search.FontSize = 13; search.Background = panel; search.Foreground = ink; search.BorderBrush = edge; search.ToolTip = "搜索全部笔记 · Ctrl+K";
            Grid searchHost = new Grid { Width = 220 };
            TextBlock searchHint = new TextBlock { Text = "搜索笔记   Ctrl+K", Foreground = muted, FontSize = 12, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            searchHost.Children.Add(search); searchHost.Children.Add(searchHint);
            AutomationProperties.SetAutomationId(search, "SearchBox"); AutomationProperties.SetName(search, "搜索全部笔记"); search.TextChanged += delegate { searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (!loading) { searchTimer.Stop(); searchTimer.Start(); } }; top.Children.Add(searchHost);
            Button raw = Button("原文", delegate { sourceMode = !sourceMode; foreach (EditState s in states.Values) if (s.Editor != null) s.Editor.SourceMode = sourceMode; }); raw.Margin = new Thickness(14, 0, 0, 0); raw.ToolTip = "切换 Markdown 原文与大纲视图"; top.Children.Add(raw);
            topbar = new Border { Padding = new Thickness(32, 0, 20, 0), BorderBrush = edge, BorderThickness = new Thickness(0, 0, 0, 1), Background = paper, Child = top }; Grid.SetColumn(topbar, 1); Grid.SetColumnSpan(topbar, 2); layout.Children.Add(topbar);
            stream.Margin = new Thickness(46, 25, 46, 60); AutomationProperties.SetAutomationId(stream, "JournalStream");
            scroll.Content = stream; scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; scroll.Background = paper;
            scroll.ScrollChanged += delegate(object sender, ScrollChangedEventArgs e) { if (!loading && view == "journals" && e.VerticalChange > 0 && scroll.ScrollableHeight - scroll.VerticalOffset < 180) AppendJournals(7); };
            Grid.SetColumn(scroll, 1); Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            rightbar = new Border { Background = panel, BorderBrush = edge, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(18, 24, 16, 16), Child = new ScrollViewer { Content = related, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            Grid.SetColumn(rightbar, 2); Grid.SetRow(rightbar, 1); layout.Children.Add(rightbar);
            status.Foreground = muted; status.FontSize = 11; status.Margin = new Thickness(15, 5, 0, 0); AutomationProperties.SetAutomationId(status, "SaveStatus");
            Border footer = new Border { Background = panel, BorderBrush = edge, BorderThickness = new Thickness(0, 1, 0, 0), Child = status }; Grid.SetRow(footer, 2); Grid.SetColumn(footer, 1); Grid.SetColumnSpan(footer, 2); layout.Children.Add(footer);
        }
        void ShowWelcome()
        {
            stream.Children.Clear(); heading.Text = "开始记录";
            stream.Children.Add(Label("你的笔记，留在你的电脑里。", 26, ink));
            stream.Children.Add(new TextBlock { Text = "选择一个文件夹作为笔记库。每天的事项将连续呈现，内容保存为可直接打开的 Markdown 文件。", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 20) });
            stream.Children.Add(Button("选择或创建笔记库…", ChooseVault));
            stream.Children.Add(Button("使用文档文件夹中的 FlowLog 笔记库", delegate { OpenVault(VaultStore.DefaultRoot()); if (store != null) VaultStore.RememberRoot(store.Root); }));
            status.Text = "尚未选择笔记库 · 尚未保存任何笔记";
        }
        void ChooseVault()
        {
            if (states.Values.Any(delegate(EditState x) { return x.Busy || x.Pending != null || x.Failed; })) { FlushAll(); MessageBox.Show(this, "请先等待当前笔记保存完成。若有冲突，请先导出草稿或重新读取磁盘内容。", "切换笔记库"); return; }
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择笔记库文件夹（Markdown 保存在 journals 和 pages 中）"; dialog.ShowNewFolderButton = true;
                if (store != null) dialog.SelectedPath = store.Root;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { OpenVault(dialog.SelectedPath); if (store != null) VaultStore.RememberRoot(store.Root); }
            }
        }
        void OpenVault(string root)
        {
            try
            {
                VaultStore candidate = new VaultStore(root);
                List<Note> candidateNotes = candidate.ListNotes();
                List<Draft> candidateDrafts = candidate.ListDrafts();
                store = candidate; notes = candidateNotes; states.Clear();
                foreach (Draft draft in candidateDrafts)
                {
                    Note disk = notes.Find(delegate(Note n) { return String.Equals(n.Path, draft.Note.Path, StringComparison.OrdinalIgnoreCase); });
                    Note restored = draft.Note; restored.Revision = draft.BaseRevision;
                    if (disk != null) notes.Remove(disk); notes.Add(restored);
                    states[restored.Path] = new EditState { Note = restored, Pending = restored.Content, Failed = draft.Conflict, Conflict = draft.Conflict, Error = draft.Conflict ? "磁盘内容已变化，恢复的草稿未覆盖原文件" : null };
                }
                vaultLabel.Text = new DirectoryInfo(root).Name; vaultLabel.ToolTip = root; ShowJournals(); FlushAll();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开笔记库"); if (store == null) ShowWelcome(); }
        }
        void ClearView(string next, string title)
        {
            loading = true; saveTimer.Stop(); FlushAll(); view = next; heading.Text = title;
            stream.Children.Clear();
            active = null; related.Children.Clear(); scroll.ScrollToTop(); loading = false;
        }
        void ShowJournals()
        {
            if (store == null) { ShowWelcome(); return; }
            ClearView("journals", "每日笔记");
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!notes.Any(delegate(Note n) { return n.IsJournal && n.Date == today; })) notes.Add(VaultStore.CreateJournal(today));
            journalCount = 0; AppendJournals(7); UpdateStatus();
        }
        void AppendJournals(int count)
        {
            if (store == null || loading) return;
            loading = true;
            List<Note> journals = notes.Where(delegate(Note n) { return n.IsJournal; }).OrderByDescending(delegate(Note n) { return n.Date; }).ToList();
            int end = Math.Min(journalCount + count, journals.Count);
            for (int i = journalCount; i < end; i++) AddNote(journals[i]);
            journalCount = end; loading = false;
        }
        void ShowPages()
        {
            if (store == null) { ShowWelcome(); return; }
            ClearView("pages", "全部页面");
            foreach (Note n in notes.Where(delegate(Note x) { return !x.IsJournal; }).OrderBy(delegate(Note x) { return x.Title; }))
            { Note item = n; stream.Children.Add(Button(item.Title, delegate { OpenNote(item); })); }
            if (!notes.Any(delegate(Note n) { return !n.IsJournal; })) stream.Children.Add(Label("还没有页面。用 [[页面名]] 建立链接，或点击左侧新建页面。", 15, muted));
        }
        void ShowTasks()
        {
            if (store == null) { ShowWelcome(); return; }
            ClearView("tasks", "待办任务");
            foreach (Note n in notes.OrderByDescending(delegate(Note x) { return x.Date; }))
            {
                string[] lines = CurrentText(n).Split('\n');
                for (int i = 0; i < lines.Length; i++) if (Regex.IsMatch(lines[i], @"^\s*-?\s*(TODO|DOING|NOW|LATER|WAITING)\b|^\s*-\s+\[ \]"))
                { Note item = n; int line = i + 1; stream.Children.Add(Button(lines[i].Trim() + "  ·  " + n.Title, delegate { OpenNote(item); EditorFor(item).GoToLine(line); })); }
            }
            if (stream.Children.Count == 0) stream.Children.Add(Label("暂无待办。按 Ctrl+Enter 切换当前块的任务状态。", 15, muted));
        }
        void ShowSearch()
        {
            if (store == null) return;
            string query = search.Text.Trim(); ClearView("search", "搜索结果");
            foreach (Note n in notes.Where(delegate(Note x) { return x.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || CurrentText(x).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }))
            {
                Note item = n; stream.Children.Add(Button(item.Title, delegate { OpenNote(item); }));
                string content = CurrentText(n); int index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase); if (index < 0) index = 0;
                stream.Children.Add(new TextBlock { Text = content.Substring(Math.Max(0, index - 35), Math.Min(180, content.Length - Math.Max(0, index - 35))).Replace('\n', ' '), Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 16) });
            }
            if (stream.Children.Count == 0) stream.Children.Add(Label("没有匹配的笔记。", 15, muted));
        }
        void OpenNote(Note note)
        {
            ClearView("note", note.IsJournal ? "每日笔记" : "页面"); AddNote(note); active = note; RefreshRelated();
            OutlineEditor ed = EditorFor(note); if (ed != null) ed.Focus();
        }
        OutlineEditor EditorFor(Note n) { EditState state; return states.TryGetValue(n.Path, out state) ? state.Editor : null; }
        string CurrentText(Note n) { EditState s; return states.TryGetValue(n.Path, out s) && s.Pending != null ? s.Pending : n.Content ?? ""; }
        void AddNote(Note note)
        {
            EditState s;
            if (!states.TryGetValue(note.Path, out s)) { s = new EditState { Note = note }; states[note.Path] = s; }
            Border block = new Border { Padding = new Thickness(0, 0, 0, 30), Margin = new Thickness(0, 0, 0, 24), BorderBrush = edge, BorderThickness = new Thickness(0, 0, 0, 1) };
            StackPanel column = new StackPanel(); block.Child = column;
            string title = note.Title;
            DateTime date;
            if (note.IsJournal && DateTime.TryParseExact(note.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) title = date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")) + (date.Date == DateTime.Today ? "  ·  今天" : "");
            TextBlock caption = Label(title, 25, ink); caption.FontWeight = FontWeights.SemiBold; caption.Margin = new Thickness(0, 0, 0, 17); column.Children.Add(caption);
            OutlineEditor editor = s.Editor;
            if (editor == null)
            {
            editor = new OutlineEditor();
            editor.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); editor.FontSize = 16; editor.Foreground = ink; editor.Background = paper;
            editor.Padding = new Thickness(0, 5, 0, 10); editor.BorderThickness = new Thickness(0); editor.WordWrap = true; editor.MinHeight = 100;
            editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; editor.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            editor.PageTitles = notes.Select(delegate(Note n) { return n.Title; }).ToList(); editor.SourceMode = sourceMode; editor.Text = String.IsNullOrEmpty(CurrentText(note)) ? "- " : CurrentText(note);
            if (String.IsNullOrEmpty(CurrentText(note))) editor.CaretOffset = editor.Text.Length;
            editor.ContextMenu = EditorMenu(editor);
            AutomationProperties.SetAutomationId(editor, "NoteEditor"); AutomationProperties.SetName(editor, "NoteEditor:" + note.Path);
            s.Editor = editor; EditState state = s;
            editor.TextChanged += delegate { state.Pending = editor.Text; state.Note.Content = editor.Text; if (!state.Conflict) state.Failed = false; PersistDraft(state); saveTimer.Stop(); saveTimer.Start(); UpdateStatus(); };
            editor.GotKeyboardFocus += delegate { active = note; RefreshRelated(); };
            editor.LinkRequested += delegate(object sender, LinkEventArgs e) { FollowLink(e.Title, e.IsBlockRef); };
            }
            else
            {
                Panel previous = editor.Parent as Panel;
                if (previous != null) previous.Children.Remove(editor);
                editor.Foreground = ink; editor.Background = paper; editor.SourceMode = sourceMode;
                editor.PageTitles = notes.Select(delegate(Note n) { return n.Title; }).ToList();
            }
            column.Children.Add(editor); stream.Children.Add(block);
            if (active == null) { active = note; RefreshRelated(); }
        }
        ContextMenu EditorMenu(OutlineEditor editor)
        {
            ContextMenu menu = new ContextMenu();
            MenuItem undo = new MenuItem { Header = "撤销", Command = ApplicationCommands.Undo, CommandTarget = editor.TextArea }; menu.Items.Add(undo);
            MenuItem redo = new MenuItem { Header = "重做", Command = ApplicationCommands.Redo, CommandTarget = editor.TextArea }; menu.Items.Add(redo);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "剪切", Command = ApplicationCommands.Cut, CommandTarget = editor.TextArea });
            menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = editor.TextArea });
            menu.Items.Add(new MenuItem { Header = "粘贴", Command = ApplicationCommands.Paste, CommandTarget = editor.TextArea });
            menu.Items.Add(new Separator());
            AddMenu(menu, "切换任务状态    Ctrl+Enter", delegate { editor.ToggleTask(); });
            AddMenu(menu, "缩进块    Tab", delegate { editor.IndentSelection(false); });
            AddMenu(menu, "取消缩进    Shift+Tab", delegate { editor.IndentSelection(true); });
            AddMenu(menu, "上移块    Alt+↑", delegate { editor.MoveSelection(-1); });
            AddMenu(menu, "下移块    Alt+↓", delegate { editor.MoveSelection(1); });
            AddMenu(menu, "折叠 / 展开子块    Ctrl+.", delegate { editor.ToggleFold(); });
            AddMenu(menu, "复制块引用    Ctrl+Shift+C", delegate { string value = editor.CopyBlockReference(); if (!String.IsNullOrEmpty(value)) { try { Clipboard.SetText(value); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法复制"); } } });
            return menu;
        }
        void AddMenu(ContextMenu menu, string title, Action action)
        {
            MenuItem item = new MenuItem { Header = title }; item.Click += delegate { action(); }; menu.Items.Add(item);
        }
        Note CopyNote(Note n, string content)
        { return new Note { Path = n.Path, Title = n.Title, Content = content, Revision = n.Revision, Date = n.Date, IsJournal = n.IsJournal, HasBom = n.HasBom }; }
        void PersistDraft(EditState state)
        {
            if (state.Pending == null || store == null) return;
            try { store.SaveDraft(CopyNote(state.Note, state.Pending), state.Note.Revision); }
            catch (Exception ex) { state.Failed = true; state.Error = "无法写入恢复草稿：" + ex.Message; }
        }
        void FlushAll()
        {
            if (store == null) return;
            foreach (EditState s in states.Values.ToList()) { if ((s.Failed || s.Conflict) && s.Pending != null) { try { store.SaveDraft(CopyNote(s.Note, s.Pending), s.Note.Revision); } catch { } } else StartSave(s); }
            UpdateStatus();
        }
        void StartSave(EditState state)
        {
            if (state.Busy || state.Pending == null || state.Conflict || state.Failed) return;
            string text = state.Pending; string expected = state.Note.Revision; Note snapshot = CopyNote(state.Note, text); VaultStore target = store;
            state.Busy = true;
            try { target.SaveDraft(snapshot, expected); } catch (Exception ex) { state.Busy = false; state.Failed = true; state.Error = "无法写入恢复草稿：" + ex.Message; UpdateStatus(); return; }
            Task.Factory.StartNew(delegate
            {
                string revision = null; Exception failure = null;
                try { revision = target.Save(snapshot, text, expected); } catch (Exception ex) { failure = ex; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    state.Busy = false;
                    if (failure != null) { state.Failed = true; state.Conflict = failure.Message.Contains("其他程序修改") || failure.Message.Contains("冲突") || failure.Message.Contains(".flowlog/conflicts"); state.Error = failure.Message; try { target.SaveDraft(CopyNote(state.Note, state.Pending ?? text), expected); } catch { } }
                    else
                    {
                        state.Note.Revision = revision;
                        if (state.Pending == text)
                        {
                            state.Pending = null;
                            try { target.ClearDraft(state.Note.Path); } catch { }
                        }
                        else StartSave(state);
                    }
                    UpdateStatus(); if (active != null && String.Equals(active.Path, state.Note.Path, StringComparison.OrdinalIgnoreCase)) RefreshRelated();
                }));
            });
        }
        void UpdateStatus()
        {
            if (store == null) return;
            int failed = states.Values.Count(delegate(EditState s) { return s.Failed || s.Conflict; });
            bool busy = states.Values.Any(delegate(EditState s) { return s.Busy; }); bool pending = states.Values.Any(delegate(EditState s) { return s.Pending != null; });
            status.Text = failed > 0 ? "⚠ " + failed + " 篇笔记保存遇到问题 · 请查看右侧恢复操作" : busy ? "正在保存…" : pending ? "有未保存的修改…" : notes.Any(delegate(Note n) { return !String.IsNullOrEmpty(n.Revision); }) ? "已保存到本地  ·  " + new DirectoryInfo(store.Root).Name : "笔记库已打开 · 开始输入后自动保存";
            status.ToolTip = store.Root;
            status.Foreground = failed > 0 ? B("#BD643D") : muted;
        }
        void RefreshRelated()
        {
            related.Children.Clear(); if (active == null || store == null) return;
            related.Children.Add(Label("当前笔记", 12, muted)); related.Children.Add(new TextBlock { Text = active.Title, Foreground = ink, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 20), TextWrapping = TextWrapping.Wrap });
            EditState state; states.TryGetValue(active.Path, out state);
            if (state != null && state.Failed)
            {
                related.Children.Add(Label("保存受阻", 15, B("#BD643D"))); related.Children.Add(new TextBlock { Text = state.Error, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
                if (!state.Conflict) related.Children.Add(Button("重试保存    Ctrl+S", delegate { state.Failed = false; StartSave(state); UpdateStatus(); }));
                related.Children.Add(Button("导出当前草稿…", delegate { ExportDraft(state); }));
                related.Children.Add(Button("放弃修改，重新读取…", delegate { Reload(state); }));
            }
            related.Children.Add(new TextBlock { Text = "链接引用", FontSize = 12, Foreground = muted, Margin = new Thickness(0, 18, 0, 8) });
            int count = 0;
            foreach (Note n in notes)
            {
                if (String.Equals(n.Path, active.Path, StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.Matches(CurrentText(n), @"\[\[([^\]]+)\]\]").Cast<Match>().Any(delegate(Match m) { return String.Equals(m.Groups[1].Value, active.Title, StringComparison.OrdinalIgnoreCase); }))
                { Note target = n; related.Children.Add(Button(n.Title, delegate { OpenNote(target); })); count++; }
            }
            if (count == 0) related.Children.Add(Label("暂无其他笔记引用此页", 12, muted));
            related.Children.Add(new TextBlock { Text = "Markdown 文件\n" + active.Path, ToolTip = Path.Combine(store.Root, active.Path), Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 28, 0, 0) });
        }
        void ExportDraft(EditState s)
        {
            SaveFileDialog d = new SaveFileDialog { FileName = s.Note.Title + "-草稿.md", Filter = "Markdown 文件|*.md|所有文件|*.*" };
            if (d.ShowDialog(this) == true) { try { File.WriteAllText(d.FileName, CurrentText(s.Note), new System.Text.UTF8Encoding(false)); MessageBox.Show(this, "草稿已导出。原笔记仍保持冲突状态，你可以核对后重新读取。", "导出草稿"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败"); } }
        }
        void Reload(EditState state)
        {
            if (state.Busy) return;
            if (MessageBox.Show(this, "将放弃当前未保存修改，并重新读取磁盘文件。建议先导出草稿。", "重新读取", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                Note disk = store.ListNotes().Find(delegate(Note n) { return String.Equals(n.Path, state.Note.Path, StringComparison.OrdinalIgnoreCase); });
                if (disk == null) { disk = CopyNote(state.Note, ""); disk.Revision = ""; }
                store.ClearDraft(state.Note.Path); notes.Remove(state.Note); states.Remove(state.Note.Path); notes.Add(disk); OpenNote(disk); UpdateStatus();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "重新读取失败"); }
        }
        void FollowLink(string title, bool block)
        {
            if (store == null) return;
            if (block)
            {
                foreach (Note n in notes)
                { string[] lines = CurrentText(n).Split('\n'); for (int i = 0; i < lines.Length; i++) if (Regex.IsMatch(lines[i], @"^\s*id::\s*" + Regex.Escape(title) + @"\s*$")) { OpenNote(n); EditorFor(n).GoToLine(Math.Max(1, i)); return; } }
                MessageBox.Show(this, "没有找到这个块引用。", "块引用"); return;
            }
            Note page = notes.Find(delegate(Note n) { return String.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase); }); bool fresh = page == null;
            if (page == null) { try { page = VaultStore.CreatePage(title); notes.Add(page); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "创建页面失败"); return; } }
            OpenNote(page); if (fresh) { EditState created = states[page.Path]; created.Pending = page.Content; StartSave(created); }
        }
        void CreatePage()
        {
            if (store == null) { ChooseVault(); if (store == null) return; }
            string title = Ask("新建页面", "页面名称"); if (String.IsNullOrWhiteSpace(title)) return; FollowLink(title.Trim(), false);
        }
        string Ask(string title, string label)
        {
            Window d = new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 420, Height = 185, ResizeMode = ResizeMode.NoResize, Background = paper, Foreground = ink };
            StackPanel p = new StackPanel { Margin = new Thickness(22) }; p.Children.Add(Label(label, 13, muted));
            TextBox input = new TextBox { Margin = new Thickness(0, 10, 0, 8), Padding = new Thickness(7) }; p.Children.Add(input);
            Button ok = Button("创建", delegate { d.DialogResult = true; }); ok.IsDefault = true; p.Children.Add(ok); d.Content = p; d.Loaded += delegate { input.Focus(); };
            return d.ShowDialog() == true ? input.Text : null;
        }
        void ToggleTheme()
        {
            Brush[] before = new Brush[] { ink, muted, paper, panel, edge, accent };
            dark = !dark; SetPalette();
            Brush[] after = new Brush[] { ink, muted, paper, panel, edge, accent };
            ApplyPalette(this, before, after);
            foreach (EditState s in states.Values) if (s.Editor != null) { s.Editor.Background = paper; s.Editor.Foreground = ink; s.Editor.TextArea.TextView.Redraw(); }
            RefreshRelated();
        }
        Brush PaletteBrush(Brush value, Brush[] before, Brush[] after)
        {
            for (int i = 0; i < before.Length; i++) if (Object.ReferenceEquals(value, before[i])) return after[i];
            return value;
        }
        void ApplyPalette(DependencyObject element, Brush[] before, Brush[] after)
        {
            Control control = element as Control;
            if (control != null) { control.Background = PaletteBrush(control.Background, before, after); control.Foreground = PaletteBrush(control.Foreground, before, after); control.BorderBrush = PaletteBrush(control.BorderBrush, before, after); }
            Border border = element as Border;
            if (border != null) { border.Background = PaletteBrush(border.Background, before, after); border.BorderBrush = PaletteBrush(border.BorderBrush, before, after); }
            Panel container = element as Panel; if (container != null) container.Background = PaletteBrush(container.Background, before, after);
            TextBlock text = element as TextBlock; if (text != null) text.Foreground = PaletteBrush(text.Foreground, before, after);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyPalette(VisualTreeHelper.GetChild(element, i), before, after);
        }
        void ShowHelp()
        {
            MessageBox.Show(this, "Ctrl+K  搜索全部笔记\nEnter  新增块   ·   Shift+Enter  块内换行\nTab / Shift+Tab  缩进 / 取消缩进\nAlt+↑ / ↓  移动块\nCtrl+Enter  切换任务状态\nCtrl+Z / Ctrl+Y  撤销 / 重做\n[[页面名]]  页面链接   ·   ((块 ID))  块引用\n/  打开块命令\n\n每日笔记从今天起连续呈现，向下滚动查看更早内容。\n自动保存到所选笔记库。冲突时保留草稿，避免覆盖磁盘改动。", "流记使用说明");
        }
        void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { saveTimer.Stop(); foreach (EditState s in states.Values) if (!s.Conflict) s.Failed = false; FlushAll(); e.Handled = true; }
        }
        void OnClosing(object sender, CancelEventArgs e)
        {
            if (closeAllowed || store == null) return;
            if (!states.Values.Any(delegate(EditState s) { return s.Busy || s.Pending != null || s.Failed; })) return;
            e.Cancel = true;
            if (states.Values.Any(delegate(EditState s) { return s.Failed; }))
            {
                EditState failed = states.Values.First(delegate(EditState s) { return s.Failed; }); OpenNote(failed.Note);
                MessageBox.Show(this, "仍有笔记未能保存。请先在右侧导出草稿，或核对后选择放弃修改并重新读取。", "尚未保存"); return;
            }
            closingRequested = true; FlushAll(); closeTimer.Start();
        }
        void TryFinishClose()
        {
            if (!closingRequested) { closeTimer.Stop(); return; }
            if (states.Values.Any(delegate(EditState s) { return s.Failed; })) { closingRequested = false; closeTimer.Stop(); status.Text = "关闭已暂停：请处理未保存的笔记"; return; }
            FlushAll(); if (states.Values.Any(delegate(EditState s) { return s.Busy || s.Pending != null; })) return;
            closeTimer.Stop(); closeAllowed = true; Close();
        }
    }
}





