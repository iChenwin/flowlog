using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlowLog.Native
{
    public static class MainWindowTests
    {
        static int count;
        static void Require(bool condition, string description)
        {
            if (!condition) throw new Exception("MainWindow integration test failed: " + description);
            count++;
        }
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            T own = parent as T; if (own != null) yield return own;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        static void Wait(Func<bool> predicate, string description)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!predicate() && clock.ElapsedMilliseconds < 8000) Pump(20);
            Require(predicate(), description);
        }
        static MainWindow Open(string root)
        {
            MainWindow window = new MainWindow(root) { ShowInTaskbar = false, ShowActivated = false };
            window.Show(); window.UpdateLayout(); Pump(40); return window;
        }
        static OutlineEditor Editor(MainWindow window)
        {
            return Descendants<OutlineEditor>(window).First();
        }
        static string Status(MainWindow window)
        {
            return Descendants<TextBlock>(window).First(delegate(TextBlock b) { return AutomationProperties.GetAutomationId(b) == "SaveStatus"; }).Text;
        }
        static void Click(MainWindow window, string content)
        {
            Button button = Descendants<Button>(window).First(delegate(Button b) { return Convert.ToString(b.Content).Contains(content); });
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); Pump(25);
        }
        static void DisposeWindow(MainWindow window)
        {
            if (window == null || !window.IsVisible) return;
            typeof(MainWindow).GetField("closeAllowed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
            window.Close(); Pump(30);
        }
        static string Read(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file) : null; } catch (IOException) { return null; }
        }
        static void Screenshot(Window window, string destination)
        {
            window.UpdateLayout(); Pump(80);
            FrameworkElement surface = window.Content as FrameworkElement;
            if (surface == null) throw new InvalidOperationException("Window content must be a native visual");
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (FileStream output = File.Create(destination)) encoder.Save(output);
        }
        public static void GenerateProductScreenshot()
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(), "FlowLog-Demo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string root = Path.Combine(fixtureRoot, "日常笔记"); Directory.CreateDirectory(root);
            string native = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string project = Path.GetFullPath(Path.Combine(native, ".."));
            VaultStore demo = new VaultStore(root); MainWindow window = null;
            try
            {
                Note today = VaultStore.CreateJournal(DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                demo.Save(today, "- TODO 完成 Windows 原生版本验收\n  - 连续日记流、块编辑与键盘操作\n  - 安装包保持十几兆以内\n- 项目目标：轻量本地笔记，围绕 [[项目计划]] 推进\n- DONE 验证 Markdown 自动保存与冲突恢复\n- 代码示例：`File.WriteAllText(path, markdown);`\n- 明日安排\n  - 测试大笔记库的搜索和启动速度\n  - 收集第一批用户反馈", "");
                string[] previous = new string[] { "- 上午整理本周的阅读笔记\n  - 把零散想法放回同一条时间线\n- DONE 为周末出行列好清单\n- 晚上散步，记下三个新的想法", "- 和朋友讨论简单、可靠的笔记方式\n- TODO 周五回顾最近的项目进展\n  - 整理已经完成的事项\n  - 为下一步留一点空白", "- 开始新的一周\n- 本周关注：减少切换，让记录更自然" };
                for (int i = 0; i < previous.Length; i++)
                {
                    Note older = VaultStore.CreateJournal(DateTime.Today.AddDays(-i - 1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    demo.Save(older, previous[i], "");
                }
                Note plan = VaultStore.CreatePage("项目计划"); demo.Save(plan, "- 从 [[" + today.Title + "]] 的事项开始\n- 围绕日常记录迭代编辑体验", "");
                window = Open(root);
                Screenshot(window, Path.Combine(project, "docs", "native-screenshot.png"));
                Click(window, "切换明暗");
                Screenshot(window, Path.Combine(native, "TestResults", "native-dark.png"));
                OutlineEditor editor = Editor(window);
                SolidColorBrush foreground = editor.Foreground as SolidColorBrush;
                Require(foreground != null && foreground.Color.R > 180, "Dark theme retains readable editor foreground");
                Click(window, "全部页面");
                Button page = Descendants<Button>(window).First(delegate(Button b) { return Convert.ToString(b.Content) == "项目计划"; });
                SolidColorBrush pageInk = page.Foreground as SolidColorBrush;
                Require(pageInk != null && pageInk.Color.R > 180, "Dark theme page list retains readable button text");
                page.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(30);
                Require(Editor(window).Text.Contains(today.Title), "Page navigation opens linked project document");
                Click(window, "待办任务");
                Require(Descendants<Button>(window).Any(delegate(Button b) { return Convert.ToString(b.Content).Contains("完成 Windows 原生版本验收"); }), "Task view includes TODO journal block");
                Require(!Descendants<Button>(window).Any(delegate(Button b) { return Convert.ToString(b.Content).Contains("验证 Markdown 自动保存"); }), "Task view excludes completed journal blocks");
                TextBox search = Descendants<TextBox>(window).First(delegate(TextBox b) { return AutomationProperties.GetAutomationId(b) == "SearchBox"; });
                search.Text = "启动速度"; Pump(300);
                Require(Descendants<Button>(window).Any(delegate(Button b) { return Convert.ToString(b.Content) == today.Title; }), "Full-text search returns matching journal");
            }
            finally
            {
                DisposeWindow(window);
                string resolved = Path.GetFullPath(fixtureRoot);
                if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("FlowLog-Demo-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
            }
        }
        [STAThread]
        public static int Run()
        {
            Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Runs on STA"); count = 1;
            string root = Path.Combine(Path.GetTempPath(), "FlowLog-WindowTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string remembered = VaultStore.LoadLastRoot();
            VaultStore disk = new VaultStore(root);
            for (int i = 1; i <= 18; i++)
            {
                Note older = VaultStore.CreateJournal(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                disk.Save(older, "- TODO 核对每日事项\n  - 本地 Markdown 与原生大纲编辑\n- 记录今天的想法", "");
            }
            string today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Note journal = VaultStore.CreateJournal(today);
            string file = Path.Combine(root, journal.Path);
            MainWindow window = null;
            try
            {
                window = Open(root); OutlineEditor first = Editor(window);
                Require(Descendants<OutlineEditor>(window).Count() == 7, "Journal stream initially mounts seven native daily editors");
                Require(!File.Exists(file), "Opening empty vault does not falsely save placeholder");
                Require(first.Text == "- ", "New journal exposes editable outline block");
                first.CaretOffset = first.Text.Length; first.TextArea.PerformTextInput("Alpha 中文");
                Require(disk.ListDrafts().Any(delegate(Draft d) { return d.Note.Path == journal.Path && d.Note.Content == "- Alpha 中文"; }), "Native text input persists recovery draft synchronously");
                Wait(delegate { return Read(file) == "- Alpha 中文" && Status(window).Contains("已保存到本地"); }, "Debounced async save writes exact native document");
                first.Select(2, 5);
                int caret = first.CaretOffset;
                Click(window, "全部页面"); Click(window, "每日笔记");
                OutlineEditor returned = Editor(window);
                Require(Object.ReferenceEquals(first, returned), "Navigation reuses original native editor instance");
                Require(returned.CaretOffset == caret, "Navigation preserves caret offset");
                Require(returned.SelectionStart == 2 && returned.SelectionLength == 5, "Navigation preserves native text selection");
                ScrollViewer journalScroll = Descendants<ScrollViewer>(window).First(delegate(ScrollViewer v) { StackPanel p = v.Content as StackPanel; return p != null && AutomationProperties.GetAutomationId(p) == "JournalStream"; });
                journalScroll.ScrollToBottom(); Pump(120);
                Require(Descendants<OutlineEditor>(window).Count() >= 14, "Scrolling near end appends earlier journals incrementally");
                Require(Object.ReferenceEquals(first, Descendants<OutlineEditor>(window).First()) && first.SelectionLength == 5, "Appending journals preserves current editor instance and selection");
                returned.Undo(); Require(returned.Text == "- ", "Native undo history survives page navigation");
                returned.Redo(); Require(returned.Text == "- Alpha 中文", "Native redo survives page navigation");
                returned.CaretOffset = returned.Text.Length; returned.TextArea.PerformTextInput(" · closing保存");
                string final = returned.Text; window.Close();
                Wait(delegate { return !window.IsVisible && Read(file) == final; }, "Closing waits for last pending content to reach disk");
                window = Open(root);
                Require(Editor(window).Text == final, "Restart restores exact saved content");
                window.Close(); Wait(delegate { return !window.IsVisible; }, "Clean window closes without blocked save");

                Note saved = disk.ListNotes().Single(delegate(Note n) { return n.Path == journal.Path; });
                saved.Content += "\n- 意外退出恢复"; disk.SaveDraft(saved, saved.Revision);
                window = Open(root);
                Require(Editor(window).Text == saved.Content, "Restart loads persisted unsaved recovery draft");
                string recovered = saved.Content;
                Wait(delegate { return Read(file) == recovered && !disk.ListDrafts().Any(); }, "Recovered draft safely saves against original base revision");
                Screenshot(window, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "TestResults", "native-window.png"));

                string external = "- external editor content 外部修改";
                File.WriteAllText(file, external, new System.Text.UTF8Encoding(false));
                OutlineEditor conflicted = Editor(window); conflicted.CaretOffset = conflicted.Text.Length; conflicted.TextArea.PerformTextInput(" + local edit");
                string local = conflicted.Text;
                Wait(delegate { return Status(window).Contains("保存遇到问题"); }, "External edit becomes visible save conflict");
                Require(Read(file) == external, "Conflict never overwrites external disk contents");
                Require(disk.ListDrafts().Any(delegate(Draft d) { return d.Note.Content == local && d.Conflict; }), "Conflict preserves exact local document in recovery draft");
                conflicted.TextArea.PerformTextInput(" + newer edit");
                string latest = conflicted.Text;
                Require(disk.ListDrafts().Any(delegate(Draft d) { return d.Note.Content == latest && d.Conflict; }), "Editing during conflict immediately refreshes recovery draft");
                Require(Read(file) == external, "Further conflicted edits still preserve external file");
                Require(VaultStore.LoadLastRoot() == remembered, "Explicit isolated vault never changes remembered user vault");
                GenerateProductScreenshot();
                return count;
            }
            finally
            {
                DisposeWindow(window);
                foreach (Draft draft in disk.ListDrafts()) disk.ClearDraft(draft.Note.Path);
                string temp = Path.GetFullPath(Path.GetTempPath()); string resolved = Path.GetFullPath(root);
                if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("FlowLog-WindowTests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
            }
        }
    }
}
