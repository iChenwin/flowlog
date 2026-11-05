using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlowLog.Native
{
    public sealed class LinkEventArgs : EventArgs
    {
        public string Title { get; private set; }
        public bool IsBlockRef { get; private set; }
        public int Line { get; private set; }
        public LinkEventArgs(string title, bool isBlockRef, int line)
        { Title = title; IsBlockRef = isBlockRef; Line = line; }
    }

    public sealed class OutlineEditor : TextEditor
    {
        private readonly FoldingManager folding;
        private readonly DispatcherTimer foldTimer;
        private CompletionWindow completion;
        private bool sourceMode, composing;
        private ITextSourceVersion codeVersion;
        private HashSet<int> codeLineCache;
        private readonly OutlinePresentationGenerator presentation;
        private readonly BlockGripMargin gripMargin;
        private readonly FoldMarkerMargin foldMargin;
        private readonly GuideLineRenderer guides;
        private const string BlockDragFormat = "FlowLog.Native.OutlineBlock.v1";
        public IEnumerable<string> PageTitles { get; set; }
        public event EventHandler<LinkEventArgs> LinkRequested;

        public bool SourceMode
        {
            get { return sourceMode; }
            set
            {
                sourceMode = value;
                try
                {
                    ShowLineNumbers = value;
                    if (gripMargin != null) gripMargin.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                    if (foldMargin != null) foldMargin.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                    TextArea.TextView.Redraw();
                }
                catch
                {
                    // 首次切换到源码模式时，AvalonEdit 会惰性创建 LineNumberMargin，其内部资源
                    // 解析可能抛出 TypeConverterMarkupExtension 相关异常（仅发生一次）。
                    // 核心状态 sourceMode 已同步，后续切换不再抛出，故安全忽略。
                }
            }
        }

        // Dotted outline guides are pure decoration and can be disabled for source mode
        // or tests without touching the document.
        internal bool ShowGuides
        {
            get { return guides != null && guides.Enabled; }
            set { if (guides != null) { guides.Enabled = value; TextArea.TextView.Redraw(); } }
        }

        internal List<OutlineGuide> Guides
        {
            get { return guides == null ? new List<OutlineGuide>() : guides.Segments; }
        }

        public OutlineEditor()
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");            FontSize = 16;
            Foreground = new SolidColorBrush(Color.FromRgb(67, 63, 56));
            Background = Brushes.Transparent;
            Padding = new Thickness(14, 10, 20, 30);
            WordWrap = true;
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            Options.IndentationSize = 2;
            Options.ConvertTabsToSpaces = true;
            Options.EnableHyperlinks = false;
            Options.EnableEmailHyperlinks = false;
            Options.EnableRectangularSelection = false;
            Options.EnableVirtualSpace = false;
            Options.InheritWordWrapIndentation = true;
            TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(228, 242, 255));
            TextArea.SelectionForeground = new SolidColorBrush(Color.FromRgb(67, 63, 56));
            TextArea.SelectionCornerRadius = 2;
            presentation = new OutlinePresentationGenerator(this);
            TextArea.TextView.ElementGenerators.Add(presentation);
            TextArea.TextView.LineTransformers.Add(new OutlineColorizer(this));
            guides = new GuideLineRenderer(this);
            TextArea.TextView.BackgroundRenderers.Add(guides);
            gripMargin = new BlockGripMargin(this);
            TextArea.LeftMargins.Insert(0, gripMargin);
            folding = FoldingManager.Install(TextArea);
            // The default fold margin draws a +/- box plus a solid connector spanning
            // the whole subtree. FlowLog renders a small marker per fold instead, so
            // drop the stock margin and draw the marker in FoldMarkerMargin below.
            foreach (FoldingMargin legacy in TextArea.LeftMargins.OfType<FoldingMargin>().ToList())
                TextArea.LeftMargins.Remove(legacy);
            foldMargin = new FoldMarkerMargin(this);
            TextArea.LeftMargins.Insert(1, foldMargin);
            foldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            foldTimer.Tick += delegate { foldTimer.Stop(); RefreshFoldings(); };
            TextChanged += delegate { foldTimer.Stop(); foldTimer.Start(); };
            TextArea.Caret.PositionChanged += delegate { TextArea.TextView.Redraw(); };
            TextArea.PreviewKeyDown += HandleKey;
            TextArea.TextEntered += HandleTextEntered;
            TextArea.TextEntering += HandleTextEntering;
            TextArea.PreviewMouseLeftButtonDown += HandleLinkClick;
            AllowDrop = true;
            PreviewDragOver += HandleBlockDragOver;
            PreviewDrop += HandleBlockDrop;
            TextCompositionManager.AddPreviewTextInputStartHandler(TextArea, delegate { composing = true; });
            TextCompositionManager.AddPreviewTextInputUpdateHandler(TextArea, delegate { composing = true; });
            TextArea.PreviewKeyUp += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape)) composing = false;
            };
            TextArea.LostKeyboardFocus += delegate { composing = false; };
            TextArea.PreviewTextInput += delegate
            {
                // The IME's final Enter belongs to the composition; defer clearing
                // until WPF has completed dispatch of this input event.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { composing = false; }));
            };
            PageTitles = new string[0];
        }

        internal bool IsCodeLine(int line)
        {
            if (!Object.ReferenceEquals(codeVersion, Document.Version))
            {
                codeVersion = Document.Version;
                codeLineCache = Outline.CodeLines(Text);
            }
            return codeLineCache != null && codeLineCache.Contains(line);
        }

        private void HandleKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape)) composing = false;
            if (e.Key == Key.ImeProcessed || composing || IsReadOnly) return;
            if (completion != null && (e.Key == Key.Enter || e.Key == Key.Tab || e.Key == Key.Escape || e.Key == Key.Up || e.Key == Key.Down)) return;
            ModifierKeys mods = Keyboard.Modifiers;
            if (sourceMode && ((e.Key == Key.Enter && (mods & ModifierKeys.Control) == 0) || e.Key == Key.Tab || e.Key == Key.Back)) return;
            if (IsCodeLine(TextArea.Caret.Line) && (e.Key == Key.Enter || e.Key == Key.Tab || e.Key == Key.Back)) return;
            if (e.Key == Key.Enter && (mods & ModifierKeys.Control) != 0) { ToggleTask(); e.Handled = true; }
            else if (e.Key == Key.Enter && (mods == ModifierKeys.None || mods == ModifierKeys.Shift)) { SplitBlock(mods == ModifierKeys.Shift); e.Handled = true; }
            else if (e.Key == Key.Tab && (mods == ModifierKeys.None || mods == ModifierKeys.Shift)) { IndentSelection(mods == ModifierKeys.Shift); e.Handled = true; }
            else if ((e.Key == Key.Up || e.Key == Key.Down) && mods == ModifierKeys.Alt) { MoveSelection(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                string reference = CopyBlockReference();
                if (!String.IsNullOrEmpty(reference))
                {
                    try { Clipboard.SetText(reference); } catch (System.Runtime.InteropServices.ExternalException) { }
                }
                e.Handled = true;
            }
            else if (e.Key == Key.OemPeriod && mods == ModifierKeys.Control) { ToggleFold(); e.Handled = true; }
            else if (e.Key == Key.Back && mods == ModifierKeys.None && SelectionLength == 0) e.Handled = MergeAtBlockStart();
        }

        private sealed class SelectionState
        {
            public int Start, Length, Caret;
            public List<int> ClosedFolds;
        }

        private SelectionState CaptureSelection()
        {
            RefreshFoldings();
            return new SelectionState { Start = SelectionStart, Length = SelectionLength, Caret = CaretOffset,
                ClosedFolds = folding.AllFoldings.Where(delegate(FoldingSection fold) { return fold.IsFolded; }).Select(delegate(FoldingSection fold) { return fold.StartOffset; }).ToList() };
        }

        private void RestoreSelection(SelectionState value)
        {
            RestoreFolds(value.ClosedFolds);
            int start = Math.Max(0, Math.Min(Document.TextLength, value.Start));
            int length = Math.Max(0, Math.Min(Document.TextLength - start, value.Length));
            Select(start, length);
            if (length == 0) CaretOffset = Math.Max(0, Math.Min(Document.TextLength, value.Caret));
        }

        private void RestoreFolds(IEnumerable<int> closed)
        {
            RefreshFoldings();
            HashSet<int> positions = new HashSet<int>(closed ?? new int[0]);
            foreach (FoldingSection fold in folding.AllFoldings) fold.IsFolded = positions.Contains(fold.StartOffset);
        }

        private sealed class SelectionUndo : IUndoableOperation
        {
            private readonly OutlineEditor owner;
            private readonly SelectionState state;
            private readonly bool onUndo;
            public SelectionUndo(OutlineEditor owner, SelectionState state, bool onUndo)
            { this.owner = owner; this.state = state; this.onUndo = onUndo; }
            public void Undo() { if (onUndo) owner.RestoreSelection(state); }
            public void Redo() { if (!onUndo) owner.RestoreSelection(state); }
        }

        private void Edit(Action change)
        {
            if (IsReadOnly) return;
            SelectionState before = CaptureSelection();
            Document.UndoStack.StartUndoGroup();
            try
            {
                Document.UndoStack.Push(new SelectionUndo(this, before, true));
                using (Document.RunUpdate()) change();
                Document.UndoStack.Push(new SelectionUndo(this, CaptureSelection(), false));
            }
            finally { Document.UndoStack.EndUndoGroup(); }
            TextArea.Caret.BringCaretToView();
        }

        private void ReplaceSelection(string value)
        {
            int start = SelectionStart;
            Document.Replace(start, SelectionLength, value);
            Select(start + value.Length, 0);
            CaretOffset = start + value.Length;
        }

        private void SplitBlock(bool continuation)
        {
            OutlineBlock block = Outline.At(Text, SelectionStart);
            string newline = Outline.Newline(Text);
            DocumentLine line = Document.GetLineByOffset(SelectionStart);
            if (block == null)
            {
                Edit(delegate { ReplaceSelection(newline + (continuation ? "  " : "- ")); });
                return;
            }
            if (!continuation && SelectionLength == 0 && line.LineNumber == block.Line && String.IsNullOrWhiteSpace(block.Content))
            {
                if (block.Indent > 0) IndentSelection(true);
                else Edit(delegate { Document.Remove(block.Offset, block.PrefixLength); CaretOffset = block.Offset; });
                return;
            }
            string prefix = block.Whitespace + (continuation ? "  " : "- ");
            Edit(delegate { ReplaceSelection(newline + prefix); });
        }

        private bool MergeAtBlockStart()
        {
            OutlineBlock block = Outline.At(Text, CaretOffset);
            if (block == null || CaretOffset != block.ContentOffset) return false;
            if (block.Indent > 0) { IndentSelection(true); return true; }
            if (block.Line == 1) return true;
            DocumentLine previous = Document.GetLineByNumber(block.Line - 1);
            if (Outline.MetadataPattern.IsMatch(Document.GetText(previous))) return false;
            int join = previous.EndOffset;
            Edit(delegate
            {
                Document.Remove(join, block.ContentOffset - join);
                Select(join, 0); CaretOffset = join;
            });
            return true;
        }

        public void ToggleTask()
        {
            List<OutlineBlock> blocks = Outline.Parse(Text);
            int first = SelectionStart, last = SelectionStart + Math.Max(SelectionLength - 1, 0);
            OutlineBlock active = Outline.At(Text, first);
            if (active == null) return;
            List<OutlineBlock> targets = blocks.Where(delegate(OutlineBlock b) { return b.Offset == active.Offset || (b.Offset >= first && b.Offset <= last); }).ToList();
            Edit(delegate
            {
                int caret = CaretOffset, select = SelectionStart, length = SelectionLength;
                for (int i = targets.Count - 1; i >= 0; i--)
                {
                    OutlineBlock b = targets[i];
                    Match match = Regex.Match(b.Content, @"^(TODO|DOING|DONE)\b\s*");
                    string next = !match.Success ? "TODO " : match.Groups[1].Value == "TODO" ? "DOING " : match.Groups[1].Value == "DOING" ? "DONE " : "";
                    int removed = match.Success ? match.Length : 0;
                    Document.Replace(b.ContentOffset, removed, next);
                    int delta = next.Length - removed;
                    if (caret >= b.ContentOffset) caret = Math.Max(b.ContentOffset + next.Length, caret + delta);
                    if (length > 0 && b.ContentOffset >= select && b.ContentOffset <= select + length) length += delta;
                }
                if (length > 0) Select(select, Math.Max(0, length));
                else { Select(caret, 0); CaretOffset = caret; }
            });
        }

        public void IndentSelection(bool outdent)
        {
            string text = Text;
            OutlineBlock active = Outline.At(text, SelectionStart);
            if (active == null || (outdent && active.Indent == 0)) return;
            OutlineRange range = Outline.Selection(text, SelectionStart, SelectionLength, true);
            // Indenting the first root cannot create a meaningful parent.
            if (!outdent)
            {
                OutlineBlock previous = Outline.Parse(text).LastOrDefault(delegate(OutlineBlock b) { return b.Offset < range.Start && b.Indent <= active.Indent; });
                if (previous == null || previous.Indent < active.Indent) return;
            }
            List<OutlineLine> lines = Outline.Lines(text).Where(delegate(OutlineLine l) { return l.Offset >= range.Start && l.Offset < range.End; }).ToList();
            Edit(delegate
            {
                bool selected = SelectionLength > 0;
                int start = SelectionStart, end = SelectionStart + SelectionLength, caret = CaretOffset;
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    OutlineLine line = lines[i];
                    int remove = outdent ? line.Text.Length - Outline.RemoveIndent(line.Text).Length : 0;
                    string insert = outdent ? "" : "  ";
                    Document.Replace(line.Offset, remove, insert);
                    int delta = insert.Length - remove;
                    if (start >= line.Offset) start = Math.Max(line.Offset, start + delta);
                    if (end >= line.Offset) end = Math.Max(line.Offset, end + delta);
                    if (caret >= line.Offset) caret = Math.Max(line.Offset, caret + delta);
                }
                if (selected) Select(start, Math.Max(0, end - start));
                else { Select(caret, 0); CaretOffset = caret; }
            });
        }

        public void MoveSelection(int direction)
        {
            string text = Text;
            List<OutlineBlock> blocks = Outline.Parse(text);
            OutlineBlock active = Outline.At(text, SelectionStart);
            if (active == null) return;
            OutlineRange range = Outline.Selection(text, SelectionStart, SelectionLength, true);
            OutlineBlock neighbor = direction < 0
                ? blocks.LastOrDefault(delegate(OutlineBlock b) { return b.Offset < range.Start && b.Indent <= active.Indent; })
                : blocks.FirstOrDefault(delegate(OutlineBlock b) { return b.Offset >= range.End && b.Indent <= active.Indent; });
            if (neighbor == null || neighbor.Indent != active.Indent) return;
            MoveRange(range.Start, range.End, direction < 0 ? neighbor.Offset : neighbor.EndOffset);
        }

        // Common operation for keyboard moves and the dedicated native grip.
        // The target is a document boundary, never a visual-line character index.
        private bool MoveRange(int start, int end, int target)
        {
            if (IsReadOnly || start < 0 || end > Document.TextLength || end <= start || (target >= start && target <= end)) return false;
            string text = Text;
            bool up = target < start;
            int otherStart = up ? target : end;
            int otherEnd = up ? start : target;
            string own = text.Substring(start, end - start);
            string other = text.Substring(otherStart, otherEnd - otherStart);
            int changeStart = Math.Min(start, target);
            int changeEnd = Math.Max(end, target);
            // Swapping an unterminated final block needs a delimiter at its new
            // middle boundary. Move that delimiter from the former middle block.
            string newline = Outline.Newline(text);
            string firstText = up ? own : other;
            string secondText = up ? other : own;
            if (firstText.Length > 0 && firstText[firstText.Length - 1] != '\n' && firstText[firstText.Length - 1] != '\r')
            {
                firstText += newline;
                if (secondText.EndsWith(newline, StringComparison.Ordinal)) secondText = secondText.Substring(0, secondText.Length - newline.Length);
            }
            int newOwnStart = up ? changeStart : changeStart + firstText.Length;
            int newOtherStart = up ? changeStart + firstText.Length : changeStart;
            Func<int, int> map = delegate(int position)
            {
                if (position >= start && position < end) return newOwnStart + position - start;
                if (position >= otherStart && position < otherEnd) return newOtherStart + position - otherStart;
                if (position == text.Length && end == text.Length) return newOwnStart + own.Length;
                if (position == text.Length && otherEnd == text.Length) return newOtherStart + other.Length;
                return position;
            };
            SelectionState state = CaptureSelection();
            int mappedStart = map(state.Start), mappedEnd = map(state.Start + state.Length), mappedCaret = map(state.Caret);
            if (state.Length > 0 && state.Start >= start && state.Start + state.Length <= end)
                mappedEnd = mappedStart + Math.Min(state.Length, own.Length - (state.Start - start));
            List<int> mappedFolds = state.ClosedFolds.Select(map).ToList();
            string replacement = firstText + secondText;
            Edit(delegate
            {
                Document.Replace(changeStart, changeEnd - changeStart, replacement);
                RestoreFolds(mappedFolds);
                int selectionStart = Math.Max(0, Math.Min(Document.TextLength, Math.Min(mappedStart, mappedEnd)));
                int selectionLength = state.Length == 0 ? 0 : Math.Min(Document.TextLength - selectionStart, Math.Abs(mappedEnd - mappedStart));
                Select(selectionStart, selectionLength);
                if (state.Length == 0) CaretOffset = Math.Max(0, Math.Min(Document.TextLength, mappedCaret));
            });
            return true;
        }

        private sealed class BlockDrag
        {
            public OutlineEditor Owner;
            public ITextSourceVersion Version;
            public int Start, End, Indent;
        }

        private BlockDrag CreateBlockDrag(int line)
        {
            OutlineBlock block = Outline.Parse(Text).FirstOrDefault(delegate(OutlineBlock b) { return b.Line == line; });
            if (block == null || IsReadOnly || SourceMode) return null;
            OutlineRange selected = Outline.Selection(Text, SelectionStart, SelectionLength, true);
            bool useSelection = SelectionLength > 0 && block.Offset >= selected.Start && block.Offset < selected.End;
            OutlineBlock first = useSelection ? Outline.At(Text, selected.Start) : block;
            return new BlockDrag { Owner = this, Version = Document.Version, Start = useSelection ? selected.Start : block.Offset,
                End = useSelection ? selected.End : block.EndOffset, Indent = first.Indent };
        }

        private bool FindDropTarget(DragEventArgs e, out BlockDrag data, out int target, out double markerY)
        {
            data = e.Data.GetDataPresent(BlockDragFormat) ? e.Data.GetData(BlockDragFormat) as BlockDrag : null;
            target = 0; markerY = -1;
            if (data == null || data.Owner != this || IsReadOnly || SourceMode || !Object.ReferenceEquals(data.Version, Document.Version)) return false;
            TextView view = TextArea.TextView;
            Point point = e.GetPosition(view);
            if (point.Y < 0 || point.Y > view.ActualHeight) return false;
            Point hit = view.TranslatePoint(new Point(8, point.Y), this);
            TextViewPosition? position = GetPositionFromPoint(hit);
            if (!position.HasValue) return false;
            OutlineBlock block = Outline.At(Text, Document.GetOffset(position.Value.Location));
            // This first native drag implementation reorders siblings. Reparenting
            // remains the explicit Tab/Shift+Tab command, with predictable scope.
            if (block == null || block.Indent != data.Indent) return false;
            VisualLine visual = view.GetVisualLine(block.Line);
            if (visual == null) return false;
            double top = visual.VisualTop - view.VerticalOffset;
            bool after = point.Y >= top + visual.TextLines[0].Height / 2;
            target = after ? block.EndOffset : block.Offset;
            if (target >= data.Start && target <= data.End) return false;
            markerY = after ? view.GetVisualTopByDocumentLine(block.LastLine) - view.VerticalOffset + view.DefaultLineHeight : top;
            markerY = Math.Max(0, Math.Min(view.ActualHeight - 1, markerY));
            return true;
        }

        private void HandleBlockDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(BlockDragFormat)) return;
            BlockDrag data; int target; double marker;
            bool valid = FindDropTarget(e, out data, out target, out marker);
            e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            gripMargin.DropMarker = valid ? marker : -1;
            Point point = e.GetPosition(TextArea.TextView);
            if (valid && point.Y < 20) ScrollToVerticalOffset(Math.Max(0, VerticalOffset - 16));
            else if (valid && point.Y > TextArea.TextView.ActualHeight - 20) ScrollToVerticalOffset(VerticalOffset + 16);
        }

        private void HandleBlockDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(BlockDragFormat)) return;
            BlockDrag data; int target; double marker;
            bool valid = FindDropTarget(e, out data, out target, out marker);
            gripMargin.DropMarker = -1;
            e.Effects = valid && MoveRange(data.Start, data.End, target) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private sealed class BlockGripMargin : AbstractMargin
        {
            private readonly OutlineEditor owner;
            private Point pressedAt;
            private BlockDrag pending;
            private double dropMarker = -1;
            private static readonly Brush grip = new SolidColorBrush(Color.FromRgb(169, 181, 187));
            public double DropMarker { get { return dropMarker; } set { dropMarker = value; InvalidateVisual(); } }
            public BlockGripMargin(OutlineEditor owner)
            {
                this.owner = owner;
                Width = 18;
                Cursor = Cursors.SizeAll;
                ToolTip = "拖动手柄调整同层块顺序 · 包含子块 · 单击选择整块";
                owner.TextArea.TextView.VisualLinesChanged += delegate { InvalidateVisual(); };
                owner.TextArea.TextView.ScrollOffsetChanged += delegate { InvalidateVisual(); };
                owner.DragLeave += delegate { DropMarker = -1; };
            }
            protected override Size MeasureOverride(Size availableSize) { return new Size(18, 0); }
            protected override void OnRender(DrawingContext context)
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
                if (TextView == null || !TextView.VisualLinesValid) return;
                foreach (VisualLine visual in TextView.VisualLines)
                {
                    DocumentLine line = visual.FirstDocumentLine;
                    if (owner.IsCodeLine(line.LineNumber) || !Outline.BlockPattern.IsMatch(Document.GetText(line))) continue;
                    double y = visual.VisualTop - TextView.VerticalOffset + visual.TextLines[0].Height / 2;
                    for (int row = -1; row <= 1; row++)
                        for (int col = 0; col < 2; col++) context.DrawEllipse(grip, null, new Point(5 + col * 4, y + row * 4), 0.95, 0.95);
                }
                if (dropMarker >= 0) context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(41, 122, 143)), 2), new Point(0, dropMarker), new Point(ActualWidth, dropMarker));
            }
            private int HitLine(Point point)
            {
                if (TextView == null || !TextView.VisualLinesValid) return 0;
                foreach (VisualLine visual in TextView.VisualLines)
                {
                    double top = visual.VisualTop - TextView.VerticalOffset;
                    if (point.Y < top || point.Y > top + visual.TextLines[0].Height) continue;
                    int line = visual.FirstDocumentLine.LineNumber;
                    if (!owner.IsCodeLine(line) && Outline.BlockPattern.IsMatch(Document.GetText(visual.FirstDocumentLine))) return line;
                }
                return 0;
            }
            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonDown(e);
                int line = HitLine(e.GetPosition(this));
                pending = line == 0 ? null : owner.CreateBlockDrag(line);
                if (pending == null) return;
                pressedAt = e.GetPosition(this);
                CaptureMouse(); e.Handled = true;
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (pending == null || e.LeftButton != MouseButtonState.Pressed) return;
                Point position = e.GetPosition(this);
                if (Math.Abs(position.X - pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                BlockDrag drag = pending; pending = null; ReleaseMouseCapture();
                try { DragDrop.DoDragDrop(this, new DataObject(BlockDragFormat, drag), DragDropEffects.Move); }
                finally { DropMarker = -1; }
                e.Handled = true;
            }
            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                base.OnMouseLeftButtonUp(e);
                BlockDrag selected = pending; pending = null; ReleaseMouseCapture();
                if (selected != null && Object.ReferenceEquals(selected.Version, owner.Document.Version))
                {
                    owner.Select(selected.Start, selected.End - selected.Start);
                    owner.Focus(); e.Handled = true;
                }
            }
        }

        public string CopyBlockReference()
        {
            OutlineBlock block = Outline.At(Text, CaretOffset);
            if (block == null) return "";
            List<OutlineLine> lines = Outline.Lines(Text);
            // Properties belong to this block only, never to a child block.
            int ownEnd = Text.Length;
            OutlineBlock next = Outline.Parse(Text).FirstOrDefault(delegate(OutlineBlock b) { return b.Offset > block.Offset; });
            if (next != null) ownEnd = next.Offset;
            foreach (OutlineLine property in lines)
            {
                if (property.Offset <= block.Offset || property.Offset >= ownEnd || IsCodeLine(property.Number)) continue;
                Match existing = Outline.IdPattern.Match(property.Text);
                if (existing.Success) return "((" + existing.Groups[1].Value + "))";
            }
            if (IsReadOnly) return "";
            string id = Guid.NewGuid().ToString();
            OutlineLine header = lines[block.Line - 1];
            string value = Outline.Newline(Text) + block.Whitespace + "  id:: " + id;
            int savedCaret = CaretOffset, savedStart = SelectionStart, savedLength = SelectionLength;
            Edit(delegate
            {
                Document.Insert(header.EndOffset, value);
                Select(savedStart, savedLength);
                if (savedLength == 0) CaretOffset = savedCaret;
            });
            return "((" + id + "))";
        }

        public void ToggleFold()
        {
            RefreshFoldings();
            OutlineBlock block = Outline.At(Text, CaretOffset);
            if (block == null) return;
            DocumentLine line = Document.GetLineByNumber(block.Line);
            FoldingSection section = FoldingAt(line);
            if (section != null) section.IsFolded = !section.IsFolded;
            if (foldMargin != null) foldMargin.InvalidateVisual();
            TextArea.TextView.Redraw();
        }

        // A folding section starts at the parent line's end offset, so matching the
        // start offset keeps the marker on the line that owns the children.
        internal FoldingSection FoldingAt(DocumentLine line)
        {
            if (line == null) return null;
            FoldingSection section = folding.GetNextFolding(line.EndOffset);
            return section != null && section.StartOffset == line.EndOffset ? section : null;
        }

        internal enum FoldMarker { None, Expanded, Collapsed }

        // Exposed for tests: the marker this row would draw in the margin.
        internal FoldMarker MarkerForLine(int lineNumber)
        {
            if (folding == null || lineNumber < 1 || lineNumber > Document.LineCount) return FoldMarker.None;
            FoldingSection section = FoldingAt(Document.GetLineByNumber(lineNumber));
            if (section == null) return FoldMarker.None;
            return section.IsFolded ? FoldMarker.Collapsed : FoldMarker.Expanded;
        }

        // A sibling connector is drawn only while its source row is expanded; a folded
        // row hides its children and drops the connector that would bridge to the sibling.
        internal bool GuideIsDrawn(OutlineGuide guide)
        {
            return MarkerForLine(guide.FromLine) == FoldMarker.Expanded;
        }

        private void RefreshFoldings()
        {
            List<NewFolding> folds = new List<NewFolding>();
            var blocks = Outline.Parse(Text);
            for (int i = 0; i < blocks.Count; i++)
            {
                OutlineBlock block = blocks[i];
                // 只有块有直接子块（下一块缩进更深）时才显示折叠方块。
                // 非块行（如下一行不是 - 开头）不应导致折叠；Logseq 同样只对有子块的块显示折叠方块。
                bool hasChildren = i + 1 < blocks.Count && blocks[i + 1].Indent > block.Indent;
                if (!hasChildren) continue;

                DocumentLine line = Document.GetLineByNumber(block.Line);
                int end = block.EndOffset;
                if (end > 0 && Document.GetCharAt(end - 1) == '\n') end--;
                if (end > 0 && Document.GetCharAt(end - 1) == '\r') end--;
                if (end > line.EndOffset) folds.Add(new NewFolding(line.EndOffset, end) { Name = " …" });
            }
            folding.UpdateFoldings(folds.OrderBy(delegate(NewFolding f) { return f.StartOffset; }), -1);
            if (foldMargin != null) foldMargin.InvalidateVisual();
            // Sibling connectors depend on the folded state, so redraw them with the folds.
            TextArea.TextView.Redraw();
        }

        public void GoToLine(int line)
        {
            line = Math.Max(1, Math.Min(line, Document.LineCount));
            DocumentLine target = Document.GetLineByNumber(line);
            foreach (FoldingSection fold in folding.AllFoldings)
                if (fold.StartOffset < target.Offset && fold.EndOffset > target.Offset) fold.IsFolded = false;
            CaretOffset = target.Offset;
            ScrollToLine(line);
            TextArea.Caret.BringCaretToView();
            Focus();
        }

        public void InsertCommand(string command)
        {
            if (String.IsNullOrEmpty(command)) return;
            string cmd = command.Trim().TrimStart('/').ToUpperInvariant();
            OutlineBlock block = Outline.At(Text, CaretOffset);
            string value;
            if (cmd == "TODO" || cmd == "DOING" || cmd == "DONE")
            {
                if (block == null) value = cmd + " ";
                else
                {
                    Match task = Regex.Match(block.Content, @"^(TODO|DOING|DONE)\b\s*");
                    Edit(delegate
                    {
                        int position = CaretOffset;
                        Document.Replace(block.ContentOffset, task.Success ? task.Length : 0, cmd + " ");
                        CaretOffset = Math.Max(block.ContentOffset + cmd.Length + 1, position + cmd.Length + 1 - (task.Success ? task.Length : 0));
                    });
                    return;
                }
            }
            else if (cmd == "标题" || cmd == "HEADING" || cmd == "H1") value = "## ";
            else if (cmd == "代码" || cmd == "CODE")
            {
                string whitespace = block == null ? "  " : block.Whitespace + "  ";
                string nl = Outline.Newline(Text);
                value = "```" + nl + whitespace + nl + whitespace + "```";
            }
            else if (cmd == "时间戳" || cmd == "TIME" || cmd == "TIMESTAMP") value = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            else value = command;
            Edit(delegate
            {
                int start = SelectionStart;
                ReplaceSelection(value);
                if (cmd == "代码" || cmd == "CODE") CaretOffset = start + 3 + Outline.Newline(Text).Length + (block == null ? 2 : block.Whitespace.Length + 2);
            });
        }

        private void HandleLinkClick(object sender, MouseButtonEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            TextViewPosition? position = GetPositionFromPoint(e.GetPosition(this));
            if (!position.HasValue) return;
            DocumentLine line = Document.GetLineByNumber(position.Value.Line);
            int offset = Document.GetOffset(position.Value.Location) - line.Offset;
            foreach (Match match in Outline.LinkPattern.Matches(Document.GetText(line)))
            {
                if (offset < match.Index || offset > match.Index + match.Length) continue;
                bool isBlock = match.Groups[2].Success;
                string title = match.Groups[1].Success ? match.Groups[1].Value : isBlock ? match.Groups[2].Value : match.Groups[3].Value;
                EventHandler<LinkEventArgs> handler = LinkRequested;
                if (handler != null) handler(this, new LinkEventArgs(title, isBlock, line.LineNumber));
                e.Handled = true;
                break;
            }
        }

        private void HandleTextEntering(object sender, TextCompositionEventArgs e)
        {
            if (completion != null && e.Text.Length > 0 && !Char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != ' ' && e.Text[0] != '-' && e.Text[0] != '_')
                completion.CompletionList.RequestInsertion(e);
        }

        private void HandleTextEntered(object sender, TextCompositionEventArgs e)
        {
            if (completion != null || IsReadOnly) return;
            if (IsCodeLine(TextArea.Caret.Line)) return;
            int offset = CaretOffset;
            if (e.Text == "[" && offset >= 2 && Document.GetText(offset - 2, 2) == "[[")
            {
                List<CompletionEntry> entries = (PageTitles ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(delegate(string title) { return title; }).Select(delegate(string title) { return new CompletionEntry(this, title, false); }).ToList();
                ShowCompletion(entries, offset);
            }
            else if (e.Text == "/")
            {
                OutlineBlock block = Outline.At(Text, offset);
                if (block != null && offset == block.ContentOffset + 1)
                {
                    string[] commands = { "TODO", "DOING", "DONE", "标题", "代码", "时间戳" };
                    ShowCompletion(commands.Select(delegate(string cmd) { return new CompletionEntry(this, cmd, true); }), offset);
                }
            }
        }

        private void ShowCompletion(IEnumerable<CompletionEntry> entries, int start)
        {
            CompletionWindow window = new CompletionWindow(TextArea);
            foreach (CompletionEntry entry in entries) window.CompletionList.CompletionData.Add(entry);
            if (window.CompletionList.CompletionData.Count == 0) return;
            completion = window;
            window.StartOffset = start;
            window.CloseWhenCaretAtBeginning = true;
            window.Closed += delegate { if (completion == window) completion = null; };
            window.Show();
        }

        private sealed class CompletionEntry : ICompletionData
        {
            private readonly OutlineEditor owner;
            private readonly bool command;
            public CompletionEntry(OutlineEditor owner, string text, bool command)
            { this.owner = owner; Text = text; this.command = command; }
            public ImageSource Image { get { return null; } }
            public string Text { get; private set; }
            public object Content { get { return command ? "/" + Text : Text; } }
            public object Description { get { return command ? "插入块命令 · Enter 确认" : "链接到页面 · Ctrl + 单击打开"; } }
            public double Priority { get { return 0; } }
            public void Complete(TextArea textArea, ISegment segment, EventArgs insertionRequestEventArgs)
            {
                owner.Edit(delegate
                {
                    int start = segment.Offset;
                    if (command)
                    {
                        if (start > 0 && owner.Document.GetCharAt(start - 1) == '/') start--;
                        owner.Document.Remove(start, segment.EndOffset - start);
                        owner.CaretOffset = start;
                        owner.InsertCommand(Text);
                    }
                    else
                    {
                        bool closed = segment.EndOffset + 2 <= owner.Document.TextLength && owner.Document.GetText(segment.EndOffset, 2) == "]]";
                        string value = Text + (closed ? "" : "]]");
                        owner.Document.Replace(segment.Offset, segment.Length, value);
                        owner.CaretOffset = segment.Offset + value.Length + (closed ? 2 : 0);
                    }
                });
            }
        }
    }

    internal sealed class OutlinePresentationGenerator : VisualLineElementGenerator
    {
        private readonly OutlineEditor owner;
        public OutlinePresentationGenerator(OutlineEditor owner) { this.owner = owner; }
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (owner.SourceMode) return -1;
            DocumentLine line = CurrentContext.Document.GetLineByOffset(startOffset);
            int last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
            while (line != null && line.LineNumber <= last)
            {
                if (owner.IsCodeLine(line.LineNumber)) { line = line.NextLine; continue; }
                string text = CurrentContext.Document.GetText(line);
                int best = -1;
                // Keep metadata editable when the caret is on that line.
                if (Outline.MetadataPattern.IsMatch(text) && owner.TextArea.Caret.Line != line.LineNumber)
                {
                    int metaOffset = line.Offset;
                    if (metaOffset >= startOffset && (best == -1 || metaOffset < best)) best = metaOffset;
                }
                Match block = Outline.BlockPattern.Match(text);
                if (block.Success)
                {
                    // Widen the leading indentation for display only: the two Markdown
                    // spaces stay in the document, but each level occupies Logseq's
                    // ~29px rhythm. This keeps file compatibility and native offsets.
                    string whitespace = block.Groups[1].Value;
                    if (whitespace.Length > 0 && line.Offset >= startOffset && (best == -1 || line.Offset < best)) best = line.Offset;
                    int bulletOffset = line.Offset + whitespace.Length;
                    if (bulletOffset >= startOffset && (best == -1 || bulletOffset < best)) best = bulletOffset;
                }
                string inlineText = Outline.WithoutInlineCode(text);
                foreach (Match arrow in Outline.ArrowPattern.Matches(inlineText))
                {
                    int arrowOffset = line.Offset + arrow.Index;
                    if (arrowOffset >= startOffset && (best == -1 || arrowOffset < best)) best = arrowOffset;
                }
                foreach (Match mention in Outline.MentionPattern.Matches(inlineText))
                {
                    int mentionOffset = line.Offset + mention.Index;
                    if (mentionOffset >= startOffset && (best == -1 || mentionOffset < best)) best = mentionOffset;
                }
                if (best >= startOffset) return best;
                line = line.NextLine;
            }
            return -1;
        }
        public override VisualLineElement ConstructElement(int offset)
        {
            DocumentLine line = CurrentContext.Document.GetLineByOffset(offset);
            if (owner.IsCodeLine(line.LineNumber)) return null;
            string text = CurrentContext.Document.GetText(line);
            if (offset == line.Offset && Outline.MetadataPattern.IsMatch(text) && owner.TextArea.Caret.Line != line.LineNumber)
                return new FormattedTextElement("\u200B", line.Length);
            Match match = Outline.BlockPattern.Match(text);
            if (match.Success)
            {
                string whitespace = match.Groups[1].Value;
                if (whitespace.Length > 0 && offset == line.Offset)
                {
                    // Display-only indentation: replace the Markdown whitespace with a
                    // wider run of spaces so adjacent levels match Logseq's visible step.
                    return new FormattedTextElement(new string(' ', Outline.DisplayIndent(whitespace)), whitespace.Length);
                }
                if (offset == line.Offset + whitespace.Length)
                    return new FormattedTextElement("•  ", 2);
            }
            int relative = offset - line.Offset;
            if (relative >= 0 && relative + 2 <= text.Length)
            {
                string pair = text.Substring(relative, 2);
                if (pair == "->")
                    return new FormattedTextElement("\U0001F852", 2);
                if (pair == "<-")
                    return new FormattedTextElement("\U0001F850", 2);
                if (pair == "@P")
                    return new FormattedTextElement("\U0001F464", 2);
                if (pair == "@D")
                    return new FormattedTextElement("\U0001F4C5", 2);
                if (pair == "@T")
                    return new FormattedTextElement("\U0001F552", 2);
                if (pair == "@L")
                    return new FormattedTextElement("\U0001F4CD", 2);
            }
            return null;
        }
    }

    internal sealed class OutlineColorizer : DocumentColorizingTransformer
    {
        private readonly OutlineEditor owner;
        public OutlineColorizer(OutlineEditor owner) { this.owner = owner; }
        private static readonly Brush link = new SolidColorBrush(Color.FromRgb(16, 107, 163));
        private static readonly Brush muted = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        private static readonly Brush darkLink = new SolidColorBrush(Color.FromRgb(138, 187, 187));
        private static readonly Brush darkMuted = new SolidColorBrush(Color.FromRgb(147, 161, 161));
        private static readonly Brush highlight = new SolidColorBrush(Color.FromRgb(255, 255, 0));
        private static readonly Brush codeBackground = new SolidColorBrush(Color.FromRgb(242, 242, 242));
        private static readonly Brush inlineForeground = new SolidColorBrush(Color.FromRgb(67, 63, 56));
        protected override void ColorizeLine(DocumentLine line)
        {
            string text = CurrentContext.Document.GetText(line);
            SolidColorBrush background = owner.Background as SolidColorBrush;
            bool dark = background != null && background.Color.A > 0 && (background.Color.R * 299 + background.Color.G * 587 + background.Color.B * 114) < 128000;
            Brush linkBrush = dark ? darkLink : link;
            Brush mutedBrush = dark ? darkMuted : muted;
            if (owner.IsCodeLine(line.LineNumber))
            {
                if (line.Length > 0) ChangeLinePart(line.Offset, line.EndOffset, delegate(VisualLineElement element)
                {
                    element.TextRunProperties.SetTypeface(new Typeface("Cascadia Mono, Consolas, Microsoft YaHei UI"));
                    element.TextRunProperties.SetForegroundBrush(mutedBrush);
                });
                return;
            }
            string inlineText = Outline.WithoutInlineCode(text);
            foreach (Match match in Outline.LinkPattern.Matches(inlineText))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) { element.TextRunProperties.SetForegroundBrush(linkBrush); });
            foreach (Match match in Outline.BoldPattern.Matches(inlineText))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) { element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal)); });
            foreach (Match match in Outline.ItalicPattern.Matches(inlineText))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) { element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyles.Italic, element.TextRunProperties.Typeface.Weight, FontStretches.Normal)); });
            foreach (Match match in Outline.StrikethroughPattern.Matches(inlineText))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) {
                        var props = element.TextRunProperties;
                        var tdField = props.GetType().GetField("textDecorations", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (tdField != null)
                            tdField.SetValue(props, TextDecorations.Strikethrough);
                    });
            foreach (Match match in Outline.UnderlinePattern.Matches(inlineText))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) {
                        var props = element.TextRunProperties;
                        var tdField = props.GetType().GetField("textDecorations", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (tdField != null)
                            tdField.SetValue(props, TextDecorations.Underline);
                    });
            foreach (Match match in Outline.HighlightPattern.Matches(inlineText))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) {
                        var props = element.TextRunProperties;
                        var bgField = props.GetType().GetField("backgroundBrush", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (bgField != null)
                            bgField.SetValue(props, highlight);
                        props.SetForegroundBrush(inlineForeground);
                    });
            foreach (Match match in Outline.CodePattern.Matches(text))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) {
                        var props = element.TextRunProperties;
                        var bgField = props.GetType().GetField("backgroundBrush", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (bgField != null)
                            bgField.SetValue(props, codeBackground);
                        props.SetForegroundBrush(inlineForeground);
                    });

            Match task = Regex.Match(text, @"^[ \t]*- (TODO|DOING|DONE)\b");
            if (task.Success)
            {
                Group word = task.Groups[1];
                Brush color = word.Value == "DONE" ? mutedBrush : word.Value == "DOING" ? Brushes.DarkOrange : linkBrush;
                ChangeLinePart(line.Offset + word.Index, line.Offset + word.Index + word.Length,
                    delegate(VisualLineElement element) { element.TextRunProperties.SetForegroundBrush(color); element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal)); });
            }
            Match heading = Regex.Match(text, @"^[ \t]*- (#{1,6})\s+(.+)");
            if (heading.Success)
                ChangeLinePart(line.Offset + heading.Groups[1].Index, line.EndOffset,
                    delegate(VisualLineElement element) { element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal)); element.TextRunProperties.SetFontRenderingEmSize(19); });
            if (Outline.MetadataPattern.IsMatch(text))
                ChangeLinePart(line.Offset, line.EndOffset, delegate(VisualLineElement element) { element.TextRunProperties.SetForegroundBrush(mutedBrush); });
        }
    }

    // Custom left margin drawing the fold marker for each block that owns children.
    // Expanded rows point down; the stock AvalonEdit +/- box and the solid connector
    // that used to span the whole subtree are replaced by this margin.
    internal sealed class FoldMarkerMargin : AbstractMargin
    {
        private readonly OutlineEditor owner;
        private static readonly Brush lightMarker = Frozen(Color.FromRgb(150, 162, 168));
        private static readonly Brush darkMarker = Frozen(Color.FromRgb(176, 188, 194));

        public FoldMarkerMargin(OutlineEditor owner)
        {
            this.owner = owner;
            Width = 20;
            Cursor = Cursors.Hand;
            ToolTip = "折叠 / 展开子块 · Ctrl+.";
            owner.TextArea.TextView.VisualLinesChanged += delegate { InvalidateVisual(); };
            owner.TextArea.TextView.ScrollOffsetChanged += delegate { InvalidateVisual(); };
            owner.TextChanged += delegate { InvalidateVisual(); };
        }

        protected override Size MeasureOverride(Size availableSize) { return new Size(Width, 0); }

        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (TextView == null || !TextView.VisualLinesValid || owner.SourceMode) return;
            Brush brush = MarkerBrush();
            double x = ActualWidth / 2;
            foreach (VisualLine visual in TextView.VisualLines)
            {
                double y = visual.VisualTop - TextView.VerticalOffset + visual.TextLines[0].Height / 2;
                OutlineEditor.FoldMarker marker = owner.MarkerForLine(visual.FirstDocumentLine.LineNumber);
                if (marker == OutlineEditor.FoldMarker.Expanded) DrawDownTriangle(context, brush, x, y);
                else if (marker == OutlineEditor.FoldMarker.Collapsed) DrawRightTriangle(context, brush, x - 1, y);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (TextView == null || !TextView.VisualLinesValid) return;
            double pointer = e.GetPosition(this).Y;
            foreach (VisualLine visual in TextView.VisualLines)
            {
                double top = visual.VisualTop - TextView.VerticalOffset;
                if (pointer < top || pointer > top + visual.TextLines[0].Height) continue;
                FoldingSection section = owner.FoldingAt(visual.FirstDocumentLine);
                if (section == null) return;
                section.IsFolded = !section.IsFolded;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        private Brush MarkerBrush()
        {
            SolidColorBrush background = owner.Background as SolidColorBrush;
            bool dark = background != null && background.Color.A > 0
                && (background.Color.R * 299 + background.Color.G * 587 + background.Color.B * 114) < 128000;
            return dark ? darkMarker : lightMarker;
        }

        private static void DrawDownTriangle(DrawingContext context, Brush brush, double x, double y)
        {
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(x - 3.6, y - 2.4), true, true);
                path.LineTo(new Point(x + 3.6, y - 2.4), true, false);
                path.LineTo(new Point(x, y + 2.8), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(brush, null, geometry);
        }

        // Collapsed rows point right, like Logseq's closed outline chevron.
        private static void DrawRightTriangle(DrawingContext context, Brush brush, double x, double y)
        {
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(x - 2.0, y - 3.8), true, true);
                path.LineTo(new Point(x + 2.8, y), true, false);
                path.LineTo(new Point(x - 2.0, y + 3.8), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(brush, null, geometry);
        }

        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    // Dotted vertical connectors between an outline block and its child rows. They are
    // drawn behind the text, so selection, clipboard and undo keep seeing the original
    // Markdown. This replaces the solid whole-subtree line of the stock fold margin.
    internal sealed class GuideLineRenderer : IBackgroundRenderer
    {
        private readonly OutlineEditor owner;
        private static readonly Brush lightLine = Frozen(Color.FromArgb(110, 150, 162, 168));
        private static readonly Brush darkLine = Frozen(Color.FromArgb(105, 176, 188, 194));
        private static readonly DashStyle dots = Dots();
        private List<OutlineGuide> segments = new List<OutlineGuide>();
        private ITextSourceVersion version;
        private double space = -1;
        private string spaceKey;

        public GuideLineRenderer(OutlineEditor owner)
        {
            this.owner = owner;
            Enabled = true;
        }

        public KnownLayer Layer { get { return KnownLayer.Selection; } }
        public bool Enabled { get; set; }
        public List<OutlineGuide> Segments { get { Sync(); return segments; } }

        public void Sync()
        {
            if (Object.ReferenceEquals(version, owner.Document.Version)) return;
            version = owner.Document.Version;
            segments = Outline.Guides(owner.Text);
        }

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!Enabled || owner.SourceMode) return;
            Sync();
            if (segments.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            Pen pen = new Pen(IsDark() ? darkLine : lightLine, 1.4) {
                DashStyle = dots, DashCap = PenLineCap.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            int first = textView.VisualLines[0].FirstDocumentLine.LineNumber;
            int last = textView.VisualLines[textView.VisualLines.Count - 1].LastDocumentLine.LineNumber;
            double advance = Space(textView);
            foreach (OutlineGuide guide in segments)
            {
                if (guide.ToLine < first || guide.FromLine > last) continue;
                Point top, bottom;
                if (!Geometry(textView, guide, first, last, advance, out top, out bottom)) continue;
                context.DrawLine(pen, top, bottom);
            }
        }

        // The connector joins the two rows' bullet columns, running from the source
        // row's bullet to the target row's bullet. A folded source row hides its
        // children and must not draw, so the folding state is consulted here.
        private bool Geometry(TextView textView, OutlineGuide guide, int first, int last, double advance, out Point top, out Point bottom)
        {
            top = new Point(0, 0); bottom = new Point(0, 0);
            if (!owner.GuideIsDrawn(guide)) return false;
            double viewTop = textView.VerticalOffset, viewBottom = viewTop + textView.ActualHeight;
            VisualLine from = Visual(textView, guide.FromLine), to = Visual(textView, guide.ToLine);
            if (from == null && guide.FromLine >= first) return false;
            if (to == null && guide.ToLine <= last) return false;
            // The bullet sits at the start of the row's own content, so the connector's
            // column follows the source row's indent rather than its children's.
            int level = guide.Indent / 2;
            double x = level * Outline.VisualIndentSpaces * advance - advance / 2;
            if (x < 1) return false;
            double y1 = from == null ? viewTop : from.VisualTop + Row(from);
            double y2 = to == null ? viewBottom : to.VisualTop + Row(to);
            if (y1 < viewTop) y1 = viewTop;
            if (y2 > viewBottom) y2 = viewBottom;
            if (y2 - y1 < 1) return false;
            top = new Point(x, y1 - viewTop);
            bottom = new Point(x, y2 - viewTop);
            return true;
        }

        private static double Row(VisualLine visual)
        {
            // Wrapped rows put the bullet on the first text line, not at the visual middle.
            return visual.TextLines.Count > 0 ? visual.TextLines[0].Height / 2 : visual.Height / 2;
        }

        private static VisualLine Visual(TextView textView, int lineNumber)
        {
            foreach (VisualLine visual in textView.VisualLines)
                if (visual.FirstDocumentLine.LineNumber == lineNumber) return visual;
            return null;
        }

        // AvalonEdit exposes the CJK space advance only, so measure the ASCII space the
        // indentation actually renders with; the guide must land on the child bullet.
        private double Space(TextView textView)
        {
            TextFormattingMode mode = TextOptions.GetTextFormattingMode(textView);
            string key = owner.FontFamily.Source + "|" + owner.FontSize.ToString(CultureInfo.InvariantCulture) + "|" + mode;
            if (space > 0 && key == spaceKey) return space;
            spaceKey = key;
            space = -1;
            try
            {
                Typeface typeface = new Typeface(owner.FontFamily, owner.FontStyle, owner.FontWeight, owner.FontStretch);
                FormattedText text = new FormattedText("    ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, owner.FontSize, Brushes.Black, null, mode, VisualTreeHelper.GetDpi(textView).PixelsPerDip);
                if (text.WidthIncludingTrailingWhitespace > 0) space = text.WidthIncludingTrailingWhitespace / 4;
            }
            catch (ArgumentException) { }
            if (space <= 0) space = textView.WideSpaceWidth / 2;
            return space;
        }

        private bool IsDark()
        {
            SolidColorBrush background = owner.Background as SolidColorBrush;
            return background != null && background.Color.A > 0
                && (background.Color.R * 299 + background.Color.G * 587 + background.Color.B * 114) < 128000;
        }

        private static DashStyle Dots()
        {
            DashStyle style = new DashStyle(new double[] { 0.6, 2.2 }, 0);
            style.Freeze();
            return style;
        }

        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

}
