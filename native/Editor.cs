using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        private const string BlockDragFormat = "FlowLog.Native.OutlineBlock.v1";
        public IEnumerable<string> PageTitles { get; set; }
        public event EventHandler<LinkEventArgs> LinkRequested;

        public bool SourceMode
        {
            get { return sourceMode; }
            set
            {
                sourceMode = value;
                ShowLineNumbers = value;
                if (gripMargin != null) gripMargin.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                TextArea.TextView.Redraw();
            }
        }

        public OutlineEditor()
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 16;
            Foreground = new SolidColorBrush(Color.FromRgb(44, 54, 59));
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
            TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(85, 121, 173, 216));
            TextArea.SelectionCornerRadius = 2;
            presentation = new OutlinePresentationGenerator(this);
            TextArea.TextView.ElementGenerators.Add(presentation);
            TextArea.TextView.LineTransformers.Add(new OutlineColorizer(this));
            gripMargin = new BlockGripMargin(this);
            TextArea.LeftMargins.Insert(0, gripMargin);
            folding = FoldingManager.Install(TextArea);
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
            FoldingSection section = folding.GetNextFolding(line.EndOffset);
            if (section != null && section.StartOffset == line.EndOffset) section.IsFolded = !section.IsFolded;
        }

        private void RefreshFoldings()
        {
            List<NewFolding> folds = new List<NewFolding>();
            foreach (OutlineBlock block in Outline.Parse(Text))
            {
                DocumentLine line = Document.GetLineByNumber(block.Line);
                int end = block.EndOffset;
                if (end > 0 && Document.GetCharAt(end - 1) == '\n') end--;
                if (end > 0 && Document.GetCharAt(end - 1) == '\r') end--;
                if (end > line.EndOffset) folds.Add(new NewFolding(line.EndOffset, end) { Name = " …" });
            }
            folding.UpdateFoldings(folds.OrderBy(delegate(NewFolding f) { return f.StartOffset; }), -1);
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
                Match match = Outline.BlockPattern.Match(text);
                int interested = match.Success ? line.Offset + match.Groups[1].Length : -1;
                // Keep metadata editable when the caret is on that line.
                if (Outline.MetadataPattern.IsMatch(text) && owner.TextArea.Caret.Line != line.LineNumber) interested = line.Offset;
                if (interested >= startOffset) return interested;
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
            if (match.Success && offset == line.Offset + match.Groups[1].Length)
                return new FormattedTextElement("•  ", 2);
            return null;
        }
    }

    internal sealed class OutlineColorizer : DocumentColorizingTransformer
    {
        private readonly OutlineEditor owner;
        public OutlineColorizer(OutlineEditor owner) { this.owner = owner; }
        private static readonly Brush link = new SolidColorBrush(Color.FromRgb(38, 116, 139));
        private static readonly Brush muted = new SolidColorBrush(Color.FromRgb(130, 143, 151));
        private static readonly Brush darkLink = new SolidColorBrush(Color.FromRgb(118, 205, 211));
        private static readonly Brush darkMuted = new SolidColorBrush(Color.FromRgb(161, 174, 183));
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
            foreach (Match match in Outline.LinkPattern.Matches(text))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    delegate(VisualLineElement element) { element.TextRunProperties.SetForegroundBrush(linkBrush); });
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
}
