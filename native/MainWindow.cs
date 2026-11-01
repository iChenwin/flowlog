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
using WpfPath = System.Windows.Shapes.Path;
using WpfShape = System.Windows.Shapes.Shape;

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
        Button sidebarToggle, themeButton, sourceButton;
        Brush ink, muted, paper, panel, edge, accent;
        string view = "journals";
        Note active;
        int journalCount = 7;
        bool dark, sourceMode, sidebarCollapsed, closingRequested, closeAllowed, loading;

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
            ink = B(dark ? "#A4B5B6" : "#433F38"); muted = B(dark ? "#93A1A1" : "#646464");
            paper = B(dark ? "#002B36" : "#FFFFFF"); panel = B(dark ? "#023643" : "#F7F7F7");
            edge = B(dark ? "#0E5263" : "#CCCCCC"); accent = B(dark ? "#8ABBBB" : "#106BA3");
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
            side.Children.Add(SidebarButton("每日笔记", "clock", ShowJournals));
            side.Children.Add(SidebarButton("全部页面", "pages", ShowPages));
            side.Children.Add(SidebarButton("待办任务", "tasks", ShowTasks));
            side.Children.Add(new Border { Height = 1, Background = edge, Margin = new Thickness(0, 18, 0, 15) });
            side.Children.Add(SidebarButton("新建页面", "plus", CreatePage));
            side.Children.Add(SidebarButton("选择笔记库", "folder", ChooseVault));
            vaultLabel.Foreground = muted; vaultLabel.FontSize = 11; vaultLabel.TextWrapping = TextWrapping.Wrap; vaultLabel.Margin = new Thickness(12, 6, 4, 22); side.Children.Add(vaultLabel);
            themeButton = SidebarButton("切换亮暗", dark ? "dark" : "light", ToggleTheme); side.Children.Add(themeButton);
            side.Children.Add(SidebarButton("快捷键与说明", "help", ShowHelp));
            StackPanel top = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sidebarToggle = Button("", delegate { ToggleSidebar(); });
            sidebarToggle.Content = PanelLeftIcon();
            sidebarToggle.Width = 30; sidebarToggle.Padding = new Thickness(0); sidebarToggle.Margin = new Thickness(0, 0, 12, 0);
            sidebarToggle.HorizontalContentAlignment = HorizontalAlignment.Center; sidebarToggle.VerticalContentAlignment = VerticalAlignment.Center;
            sidebarToggle.ToolTip = "收起侧边栏    Ctrl+B"; sidebarToggle.IsTabStop = true;
            AutomationProperties.SetAutomationId(sidebarToggle, "SidebarToggle"); AutomationProperties.SetName(sidebarToggle, "收起侧边栏");
            top.Children.Add(sidebarToggle);
            heading.Text = "每日笔记"; heading.FontSize = 18; heading.FontWeight = FontWeights.SemiBold; heading.Foreground = ink; heading.Margin = new Thickness(0, 0, 26, 0); heading.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(heading);
            search.Width = 220; search.Padding = new Thickness(10, 6, 10, 6); search.FontSize = 13; search.Background = panel; search.Foreground = ink; search.BorderBrush = edge; search.ToolTip = "搜索全部笔记 · Ctrl+K";
            Grid searchHost = new Grid { Width = 220 };
            TextBlock searchHint = new TextBlock { Text = "搜索笔记   Ctrl+K", Foreground = muted, FontSize = 12, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            searchHost.Children.Add(search); searchHost.Children.Add(searchHint);
            AutomationProperties.SetAutomationId(search, "SearchBox"); AutomationProperties.SetName(search, "搜索全部笔记"); search.TextChanged += delegate { searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (!loading) { searchTimer.Stop(); searchTimer.Start(); } }; top.Children.Add(searchHost);
            sourceButton = Button("原文", delegate { sourceMode = !sourceMode; sourceButton.Content = sourceMode ? "渲染" : "原文"; foreach (EditState s in states.Values) if (s.Editor != null) s.Editor.SourceMode = sourceMode; }); sourceButton.Margin = new Thickness(14, 0, 0, 0); sourceButton.ToolTip = "切换 Markdown 原文与大纲视图"; top.Children.Add(sourceButton);
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
        // 16px outline icons adapted from DeepSeek Harness's product icon set.
        // A 1.5px rounded stroke matches the weight and soft terminals of the text.
        FrameworkElement IconView(string data, string automationId)
        {
            WpfPath path = new WpfPath
            {
                Data = Geometry.Parse(data),
                Stroke = ink,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
            };
            AutomationProperties.SetAutomationId(path, automationId);
            Canvas canvas = new Canvas { Width = 16, Height = 16 };
            canvas.Children.Add(path);
            return new Viewbox { Width = 16, Height = 16, Child = canvas };
        }
        FrameworkElement PanelLeftIcon()
        { return IconView("M13.5 1.5H2.5C1.94772 1.5 1.5 1.94772 1.5 2.5V13.5C1.5 14.0523 1.94772 14.5 2.5 14.5H13.5C14.0523 14.5 14.5 14.0523 14.5 13.5V2.5C14.5 1.94772 14.0523 1.5 13.5 1.5Z M5.5 1.5V14.5", "SidebarToggleIcon"); }
        string SidebarIconData(string key)
        {
            switch (key)
            {
                case "clock": return "M8 14C11.3137 14 14 11.3137 14 8C14 4.68629 11.3137 2 8 2C4.68629 2 2 4.68629 2 8C2 11.3137 4.68629 14 8 14Z M8 4.31V8.46L11 10.08";
                case "pages": return "M6.15479 4.91687H9.84543 M11.8798 9.55347V2.71525C11.8798 2.37416 11.564 2.09766 11.1744 2.09766H4.82577C4.43618 2.09766 4.12036 2.37416 4.12036 2.71525V9.55347 M2.28735 13.8022V8.84792C2.28735 8.77514 2.36262 8.72673 2.42884 8.75693L13.2936 13.7112C13.3914 13.7558 13.3596 13.9022 13.2521 13.9022H2.38735C2.33213 13.9022 2.28735 13.8575 2.28735 13.8022Z M7.46929 10.979L13.5783 8.7416C13.6435 8.7177 13.7126 8.76601 13.7126 8.83551L13.7125 13.8022C13.7125 13.8574 13.6678 13.9022 13.6125 13.9022H7.99999 M6.15479 7.2395H9.05644";
                case "tasks": return "M3.75 6.25C4.7165 6.25 5.5 5.4665 5.5 4.5C5.5 3.5335 4.7165 2.75 3.75 2.75C2.7835 2.75 2 3.5335 2 4.5C2 5.4665 2.7835 6.25 3.75 6.25Z M7.5 4.5H13.5 M3.75 13.25C4.7165 13.25 5.5 12.4665 5.5 11.5C5.5 10.5335 4.7165 9.75 3.75 9.75C2.7835 9.75 2 10.5335 2 11.5C2 12.4665 2.7835 13.25 3.75 13.25Z M7.5 11.5H13.5";
                case "plus": return "M8 2V14 M2 8H14";
                case "folder": return "M1.50439 3.11059C1.50439 2.55831 1.95211 2.1106 2.50439 2.1106H5.43389C5.67773 2.1106 5.91318 2.19969 6.09593 2.36113L7.71649 3.79265C7.89924 3.95409 8.1347 4.04319 8.3785 4.04319H13.4958C14.0481 4.04319 14.4958 4.4909 14.4958 5.04319V12.8894C14.4958 13.4417 14.0481 13.8894 13.4958 13.8894H2.50439C1.95211 13.8894 1.50439 13.4417 1.50439 12.8894V4.04319V3.11059Z M3.63501 7.66614H12.3647";
                case "light": return "M8.00007 11.8117C10.1052 11.8117 11.8117 10.1052 11.8117 8.00007C11.8117 5.89499 10.1052 4.18848 8.00007 4.18848C5.89499 4.18848 4.18848 5.89499 4.18848 8.00007C4.18848 10.1052 5.89499 11.8117 8.00007 11.8117Z M13.3899 8H15.1499 M11.8115 11.8115L13.0556 13.0556 M8 13.3901V15.1501 M4.18868 11.8115L2.94458 13.0556 M2.6101 8H0.850098 M4.18868 4.18856L2.94458 2.94446 M8 2.6101V0.850098 M11.8115 4.18856L13.0556 2.94446";
                case "dark": return "M14.1127 8.70663C14.2576 8.60602 14.4627 8.71355 14.4386 8.88834C14.2901 9.96567 13.8731 10.9912 13.2229 11.8692C12.479 12.8735 11.4613 13.6421 10.2917 14.0829C9.1222 14.5236 7.85038 14.6179 6.62865 14.3543C5.40692 14.0907 4.28709 13.4805 3.40332 12.5967C2.51955 11.7129 1.90931 10.5931 1.64572 9.37135C1.38212 8.14962 1.47635 6.87779 1.91711 5.70825C2.35787 4.5387 3.12647 3.52103 4.13083 2.77714C5.00878 2.12689 6.03433 1.70994 7.11166 1.5614C7.28645 1.5373 7.39397 1.74238 7.29337 1.88734C6.68703 2.76099 6.37885 3.81241 6.42313 4.88345C6.47392 6.11194 6.98471 7.27645 7.85413 8.14587C8.72355 9.01529 9.88805 9.52608 11.1166 9.57687C12.1876 9.62114 13.239 9.31296 14.1127 8.70663Z";
                case "help": return "M8 14.5C11.5899 14.5 14.5 11.5899 14.5 8C14.5 4.41015 11.5899 1.5 8 1.5C4.41015 1.5 1.5 4.41015 1.5 8C1.5 11.5899 4.41015 14.5 8 14.5Z M5.75 6.69646C5.75 6.29865 5.88196 5.90976 6.12919 5.57899C6.37643 5.24821 6.72783 4.99041 7.13896 4.83817C7.5501 4.68593 8.0025 4.6461 8.43895 4.72371C8.87541 4.80132 9.27632 4.99289 9.59099 5.27419C9.90566 5.55549 10.12 5.91388 10.2068 6.30406C10.2936 6.69423 10.249 7.09866 10.0787 7.4662C9.90843 7.83373 9.62004 8.14787 9.25003 8.36889C9.19476 8.4019 9.13803 8.43262 9.08004 8.46099C8.52566 8.73217 8 9.20817 8 9.82532 M8 10.7416V11.7416";
                default: return "M8 2V14 M2 8H14";
            }
        }
        Button SidebarButton(string label, string iconKey, Action action)
        {
            Button b = Button("", action);
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement icon = IconView(SidebarIconData(iconKey), "SidebarIcon_" + iconKey);
            icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = label, Foreground = ink, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            b.Content = content; b.VerticalContentAlignment = VerticalAlignment.Center;
            return b;
        }
        void UpdateThemeButton()
        {
            if (themeButton == null) return;
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement icon = IconView(SidebarIconData(dark ? "dark" : "light"), "SidebarIcon_" + (dark ? "dark" : "light"));
            icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = "切换亮暗", Foreground = ink, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            themeButton.Content = content;
            themeButton.VerticalContentAlignment = VerticalAlignment.Center;
        }
        void ToggleSidebar()
        {
            sidebarCollapsed = !sidebarCollapsed;
            if (sidebarCollapsed)
            {
                sidebar.Visibility = Visibility.Collapsed;
                layout.ColumnDefinitions[0].Width = new GridLength(0);
            }
            else
            {
                sidebar.Visibility = Visibility.Visible;
                layout.ColumnDefinitions[0].Width = new GridLength(200);
            }
            sidebarToggle.ToolTip = (sidebarCollapsed ? "展开侧边栏" : "收起侧边栏") + "    Ctrl+B";
            AutomationProperties.SetName(sidebarToggle, sidebarCollapsed ? "展开侧边栏" : "收起侧边栏");
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
            editor.TextArea.SelectionBrush = new SolidColorBrush(dark ? Color.FromRgb(51, 143, 255) : Color.FromRgb(228, 242, 255));
            editor.TextArea.SelectionForeground = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(67, 63, 56));
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
            editor.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if (scroll == null) return;
                double offset = scroll.VerticalOffset - e.Delta;
                if (offset < 0) offset = 0;
                if (offset > scroll.ScrollableHeight) offset = scroll.ScrollableHeight;
                scroll.ScrollToVerticalOffset(offset);
                e.Handled = true;
            };
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
            UpdateThemeButton();
            Brush[] after = new Brush[] { ink, muted, paper, panel, edge, accent };
            ApplyPalette(this, before, after);
            foreach (EditState s in states.Values) if (s.Editor != null) { s.Editor.Background = paper; s.Editor.Foreground = ink; s.Editor.TextArea.SelectionBrush = new SolidColorBrush(dark ? Color.FromRgb(51, 143, 255) : Color.FromRgb(228, 242, 255)); s.Editor.TextArea.SelectionForeground = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(67, 63, 56)); s.Editor.TextArea.TextView.Redraw(); }
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
            WpfShape shape = element as WpfShape;
            if (shape != null) { shape.Stroke = PaletteBrush(shape.Stroke, before, after); shape.Fill = PaletteBrush(shape.Fill, before, after); }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyPalette(VisualTreeHelper.GetChild(element, i), before, after);
        }
        void ShowHelp()
        {
            MessageBox.Show(this, "Ctrl+K  搜索全部笔记\nCtrl+B  收起 / 展开侧边栏\nEnter  新增块   ·   Shift+Enter  块内换行\nTab / Shift+Tab  缩进 / 取消缩进\nAlt+↑ / ↓  移动块\nCtrl+Enter  切换任务状态\nCtrl+Z / Ctrl+Y  撤销 / 重做\n[[页面名]]  页面链接   ·   ((块 ID))  块引用\n/  打开块命令\n\n行内渲染\n**ABC你好** 会被渲染为加粗文本\n*ABC你好* 会被渲染为斜体文本\n~~ABC你好~~ 会被渲染为删除线文本\n_ABC你好_ 会被渲染为下划线文本\n==ABC你好== 会被渲染为高亮文本\n`ABC` 会被渲染为灰底代码文本\n-> / <- 会被渲染为箭头符号\n@P / @D / @T / @L 会被渲染为符号，表示人物/日期/时间/位置\n\n每日笔记从今天起连续呈现，向下滚动查看更早内容。\n自动保存到所选笔记库。冲突时保留草稿，避免覆盖磁盘改动。", "流记使用说明");
        }
        void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if ((e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)) { ToggleSidebar(); e.Handled = true; }
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





