using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlowLog.Native
{
    public static class EditorTests
    {
        private static int count;
        private static void Require(bool condition, string description)
        {
            if (!condition) throw new Exception("Editor test failed: " + description);
            count++;
        }
        private static object Invoke(OutlineEditor editor, string method, params object[] args)
        {
            return typeof(OutlineEditor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(editor, args);
        }
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        }
        private static VisualLineElement ElementAt(OutlineEditor editor, int offset)
        {
            VisualLine line = editor.TextArea.TextView.GetVisualLine(editor.Document.GetLineByOffset(offset).LineNumber);
            int current = line.FirstDocumentLine.Offset;
            foreach (VisualLineElement element in line.Elements)
            {
                if (offset < current + element.DocumentLength) return element;
                current += element.DocumentLength;
            }
            throw new Exception("No visual element at document offset " + offset);
        }
        private static double Luminance(Color color)
        {
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        private static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        private static double Contrast(Brush foreground, Brush background)
        {
            double a = Luminance(((SolidColorBrush)foreground).Color), b = Luminance(((SolidColorBrush)background).Color);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
        private static KeyEventArgs KeyEvent(OutlineEditor editor, Window window, Key key)
        {
            KeyEventArgs result = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key);
            result.RoutedEvent = Keyboard.PreviewKeyDownEvent;
            return result;
        }
        private static void Entered(OutlineEditor editor, string text)
        {
            TextComposition composition = new TextComposition(InputManager.Current, editor.TextArea, text);
            Invoke(editor, "HandleTextEntered", editor.TextArea, new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition));
        }

        public static int Run()
        {
            count = 0;
            OutlineEditor editor = new OutlineEditor();
            Window window = new Window { Width = 760, Height = 560, Content = editor, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
            window.Show();
            try
            {
                const string original = "- first\n- second\n  - child\n- third";
                editor.Text = original;
                editor.CaretOffset = 10;
                editor.ToggleTask();
                Require(editor.Text == "- first\n- TODO second\n  - child\n- third", "Insert task at caret block");
                editor.Undo();
                Require(editor.Text == original && editor.CaretOffset == 10, "Task undo restores text and caret");
                editor.Redo();
                editor.ToggleTask();
                Require(editor.Text.Contains("- DOING second"), "Task TODO to DOING");
                editor.ToggleTask(); Require(editor.Text.Contains("- DONE second"), "Task DOING to DONE");
                editor.ToggleTask(); Require(editor.Text == original, "Task DONE to plain block");

                editor.Text = original; editor.CaretOffset = 10;
                editor.IndentSelection(false);
                Require(editor.Text == "- first\n  - second\n    - child\n- third", "Indent moves whole subtree");
                editor.IndentSelection(true); Require(editor.Text == original, "Outdent restores subtree");
                editor.Text = original; editor.Select(10, 13);
                editor.IndentSelection(false);
                Require(editor.SelectionStart == 12 && editor.SelectionLength == 15, "Cross-block selection survives indentation");
                editor.Undo(); Require(editor.Text == original && editor.SelectionStart == 10 && editor.SelectionLength == 13, "Atomic indent undo restores selection");
                editor.Redo(); Require(editor.SelectionStart == 12 && editor.SelectionLength == 15, "Atomic indent redo restores selection");

                editor.Text = original; editor.CaretOffset = 10; editor.MoveSelection(1);
                Require(editor.Text == "- first\n- third\n- second\n  - child", "Move subtree down, preserve unterminated EOF");
                editor.Undo(); Require(editor.Text == original && editor.CaretOffset == 10, "Move undo exact source and caret");
                editor.CaretOffset = editor.Text.Length; editor.MoveSelection(-1);
                Require(editor.Text == "- first\n- third\n- second\n  - child", "Move EOF block up without joining neighboring text");
                editor.Text = "- 一\r\n  - 孩子\r\n- 二\r\n- 三"; editor.Select(2, 14);
                string multiOriginal = editor.Text; int multiStart = editor.SelectionStart, multiLength = editor.SelectionLength;
                editor.MoveSelection(1); editor.Undo();
                Require(editor.Text == multiOriginal && editor.SelectionStart == multiStart && editor.SelectionLength == multiLength, "Multi-block move and undo preserve CJK CRLF selection");

                editor.Text = "- 中文事项 👩‍💻\r\n  - 子事项\r\n- 第二条"; editor.CaretOffset = 5;
                string cjk = editor.Text;
                string reference = editor.CopyBlockReference(), withId = editor.Text;
                Require(reference.StartsWith("((") && reference.Length == 40 && editor.CopyBlockReference() == reference, "Stable persistent block UUID");
                Require(editor.CaretOffset == 5 && withId.Contains("\r\n  id:: ") && withId.Contains("👩‍💻"), "Reference insertion preserves caret, CRLF and Unicode");
                editor.Undo(); Require(editor.Text == cjk, "Reference undo exactly preserves CJK and emoji");
                editor.Redo(); Require(editor.Text == withId && editor.CopyBlockReference() == reference, "Reference redo retains original UUID and Unicode");
                OutlineEditor reopened = new OutlineEditor { Text = withId }; reopened.CaretOffset = 5;
                Require(reopened.CopyBlockReference() == reference && reopened.Text == withId, "Persisted reference is reused after reopen");

                editor.Text = "- 中文甲乙"; editor.CaretOffset = 4;
                Invoke(editor, "SplitBlock", false);
                Require(editor.Text == "- 中文\n- 甲乙", "Enter splits a Unicode block at caret");
                editor.Undo(); Require(editor.Text == "- 中文甲乙" && editor.CaretOffset == 4, "Split is a single undo operation");
                Invoke(editor, "SplitBlock", true);
                Require(editor.Text == "- 中文\n  甲乙", "Shift Enter creates a block continuation");
                editor.Text = "- parent\n  - "; editor.CaretOffset = editor.Text.Length;
                Invoke(editor, "SplitBlock", false); Require(editor.Text == "- parent\n- ", "Empty child Enter promotes block");
                editor.Text = "- first\n- second"; editor.CaretOffset = 10;
                Require((bool)Invoke(editor, "MergeAtBlockStart") && editor.Text == "- firstsecond", "Backspace at block content start merges with previous block");
                editor.Undo(); Require(editor.Text == "- first\n- second" && editor.CaretOffset == 10, "Merge undo preserves block separator");

                editor.Text = "普通段落"; editor.CaretOffset = editor.Text.Length; editor.SourceMode = true;
                KeyEventArgs enter = KeyEvent(editor, window, Key.Enter);
                Invoke(editor, "HandleKey", editor.TextArea, enter);
                Require(!enter.Handled && editor.Text == "普通段落", "Source mode leaves Enter to native editor");
                editor.TextArea.PerformTextInput("\n");
                Require(editor.Text == "普通段落\n" || editor.Text == "普通段落\r\n", "Native raw paragraph newline has no forced outline marker");
                KeyEventArgs tab = KeyEvent(editor, window, Key.Tab); Invoke(editor, "HandleKey", editor.TextArea, tab);
                Require(!tab.Handled, "Source mode leaves Tab to native editor");
                editor.SourceMode = false;

                FieldInfo composing = typeof(OutlineEditor).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic);
                composing.SetValue(editor, true);
                KeyEventArgs imeEnter = KeyEvent(editor, window, Key.Enter); Invoke(editor, "HandleKey", editor.TextArea, imeEnter);
                Require(!imeEnter.Handled, "Composition Enter bypasses outline commands");
                KeyEventArgs escape = KeyEvent(editor, window, Key.Escape); Invoke(editor, "HandleKey", editor.TextArea, escape);
                Require(!(bool)composing.GetValue(editor), "Cancelling IME composition clears command guard");
                KeyEventArgs processed = KeyEvent(editor, window, Key.ImeProcessed); Invoke(editor, "HandleKey", editor.TextArea, processed);
                Require(!processed.Handled, "IME processed key passes through untouched");

                const string fenced = "- code\n  ```text\n  - literal\n  prop:: val\n  id:: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n  ```\n- actual";
                Require(Outline.Parse(fenced).Count == 2 && Outline.Parse(fenced)[1].Content == "actual", "Fenced code list markers are not outline blocks");
                Require(Outline.Parse("- ```\n  - literal\n  ```\n- next").Count == 2, "Block opening code fence retains only real blocks");
                Require(Outline.Parse("~~~md\n- literal\n~~~\n- actual").Count == 1, "Tilde fenced code excluded");
                editor.Text = fenced; editor.GoToLine(3);
                KeyEventArgs codeEnter = KeyEvent(editor, window, Key.Enter); Invoke(editor, "HandleKey", editor.TextArea, codeEnter);
                Require(!codeEnter.Handled && editor.Text == fenced, "Enter in fenced code stays native text editing");
                editor.Text = fenced; editor.CaretOffset = 2; string codeReference = editor.CopyBlockReference();
                Require(codeReference != "((aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee))" && editor.Text.Contains("  id:: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "UUID in code is neither reused nor altered");
                editor.Text = fenced; editor.CaretOffset = editor.Text.Length; Pump(window);
                VisualLine metadataCode = editor.TextArea.TextView.VisualLines.First(delegate(VisualLine line) { return line.FirstDocumentLine.LineNumber == 4; });
                Require(!metadataCode.Elements.Any(delegate(VisualLineElement element) { return element is FormattedTextElement; }), "Code properties remain visible literal text");
                VisualLine blockVisual = editor.TextArea.TextView.VisualLines.First();
                Require(blockVisual.Elements.Any(delegate(VisualLineElement element) { return element is FormattedTextElement; }), "Native reader renders bullet element");

                // Indentation is widened for display only so two adjacent outline levels
                // are separated by Logseq's ~29px rhythm while the source keeps two spaces.
                Require(Outline.DisplayIndent("  ") == Outline.VisualIndentSpaces, "One level renders with the widened indent");
                Require(Outline.DisplayIndent("    ") == Outline.VisualIndentSpaces * 2, "Two levels double the widened indent");
                Require(Outline.DisplayIndent("\t") == Outline.VisualIndentSpaces, "Tab indentation uses the same visual step");
                editor.Text = "- 父\n  - 子"; Pump(window);
                Require(editor.TextArea.TextView.GetVisualLine(2) != null && Outline.DisplayIndent("  ") > 2, "Nested row keeps Markdown spaces but renders wider");

                editor.Text = "- parent\n  - child\n- last"; Invoke(editor, "RefreshFoldings");
                Require(editor.MarkerForLine(1) == OutlineEditor.FoldMarker.Expanded, "Expanded parent shows the down triangle marker");
                editor.CaretOffset = 2; editor.ToggleFold();
                FoldingManager manager = (FoldingManager)typeof(OutlineEditor).GetField("folding", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(editor);
                Require(manager.AllFoldings.Any(delegate(FoldingSection fold) { return fold.IsFolded; }) && editor.Text == "- parent\n  - child\n- last", "Folding changes view without deleting source");
                Require(editor.MarkerForLine(1) == OutlineEditor.FoldMarker.Collapsed, "Collapsed parent keeps its fold marker");
                editor.ToggleFold();
                Require(editor.MarkerForLine(1) == OutlineEditor.FoldMarker.Expanded, "Expanding restores the down triangle marker");
                Require(editor.MarkerForLine(2) == OutlineEditor.FoldMarker.None, "Leaf row has no fold marker");
                Require(editor.MarkerForLine(5) == OutlineEditor.FoldMarker.None, "Out-of-range row has no fold marker");
                Require(editor.TextArea.LeftMargins.Any(delegate(System.Windows.UIElement margin) { return margin.GetType().Name == "FoldMarkerMargin"; }), "Custom fold marker margin replaces the stock +/- box");
                Require(!editor.TextArea.LeftMargins.Any(delegate(System.Windows.UIElement margin) { return margin is FoldingMargin; }), "Stock AvalonEdit fold margin is removed");

                const string guideSource = "- parent\n  - child\n    - grandchild\n      - deeper\n  - sibling\n- next";
                var siblingGuides = Outline.Guides(guideSource);
                Require(siblingGuides.Count == 2, "Only rows that own children start a sibling connector");
                Require(siblingGuides.Any(delegate(OutlineGuide g) { return g.FromLine == 1 && g.ToLine == 6 && g.Indent == 0; }), "Root row links to the next root row");
                Require(siblingGuides.Any(delegate(OutlineGuide g) { return g.FromLine == 2 && g.ToLine == 5 && g.Indent == 2; }), "Child row links to its own-level sibling, not a grandchild");
                Require(!siblingGuides.Any(delegate(OutlineGuide g) { return g.FromLine == 3; }), "Row with no next sibling at its indent starts no connector");
                Require(!siblingGuides.Any(delegate(OutlineGuide g) { return g.FromLine == 5 || g.FromLine == 6; }), "Leaf rows start no connector");
                Require(Outline.Guides("- ABC\n- BCD\n- CDE").Count == 0, "A leaf-only sequence draws nothing");

                const string level2 = "- root\n  - ABC\n    - a\n  - BCD\n    - b\n  - CDE";
                var level2Guides = Outline.Guides(level2);
                Require(level2Guides.Count == 2, "Two level-2 owners plus one leaf give two connectors");
                Require(level2Guides.Any(delegate(OutlineGuide g) { return g.FromLine == 2 && g.ToLine == 4 && g.Indent == 2; }), "ABC links to BCD");
                Require(level2Guides.Any(delegate(OutlineGuide g) { return g.FromLine == 4 && g.ToLine == 6 && g.Indent == 2; }), "BCD links to CDE");

                editor.Text = level2; Invoke(editor, "RefreshFoldings");
                Require(editor.Guides.Count == level2Guides.Count, "Editor exposes the sibling connectors");
                var abcGuide = editor.Guides.Single(delegate(OutlineGuide g) { return g.FromLine == 2; });
                Require(editor.GuideIsDrawn(abcGuide), "Expanded ABC draws its connector");
                editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset + 2; editor.ToggleFold(); Invoke(editor, "RefreshFoldings");
                Require(editor.MarkerForLine(2) == OutlineEditor.FoldMarker.Collapsed, "ABC folds");
                Require(!editor.GuideIsDrawn(abcGuide), "Folded ABC skips its connector");
                editor.ToggleFold(); Invoke(editor, "RefreshFoldings");
                Require(editor.GuideIsDrawn(abcGuide), "Expanding ABC restores its connector");

                // The requested scenario: adjacent level-2 rows only connect while expanded.
                const string pair = "- root\n  - ABC\n    - a1\n    - a2\n    - a3\n  - BCD\n    - b1\n    - b2\n  - CDE";
                editor.Text = pair; Invoke(editor, "RefreshFoldings");
                var abcPair = editor.Guides.Single(delegate(OutlineGuide g) { return g.FromLine == 2; });
                var bcdPair = editor.Guides.Single(delegate(OutlineGuide g) { return g.FromLine == 6; });
                Require(editor.GuideIsDrawn(abcPair) && editor.GuideIsDrawn(bcdPair), "Expanded ABC and BCD both draw");
                editor.CaretOffset = editor.Document.GetLineByNumber(2).Offset + 2; editor.ToggleFold();
                editor.CaretOffset = editor.Document.GetLineByNumber(6).Offset + 2; editor.ToggleFold();
                Invoke(editor, "RefreshFoldings");
                Require(editor.MarkerForLine(2) == OutlineEditor.FoldMarker.Collapsed && editor.MarkerForLine(6) == OutlineEditor.FoldMarker.Collapsed, "Both owners fold");
                Require(!editor.GuideIsDrawn(abcPair) && !editor.GuideIsDrawn(bcdPair), "Both folded owners skip their connectors");

                Require(editor.TextArea.TextView.BackgroundRenderers.Any(delegate(ICSharpCode.AvalonEdit.Rendering.IBackgroundRenderer renderer) { return renderer is GuideLineRenderer; }), "Guide renderer draws behind the text");
                editor.ShowGuides = false; Require(!editor.ShowGuides, "Guide rendering can be disabled");
                editor.ShowGuides = true; Require(editor.ShowGuides, "Guide rendering can be re-enabled");
                Require(editor.Text == pair, "Guide rendering never rewrites the Markdown source");
                editor.Text = "- parent\n  - child\n- last"; editor.CaretOffset = 2; editor.ToggleFold(); Invoke(editor, "RefreshFoldings");
                Require(editor.MarkerForLine(1) == OutlineEditor.FoldMarker.Collapsed, "Parent is folded before the jump");
                editor.GoToLine(2); Require(manager.AllFoldings.All(delegate(FoldingSection fold) { return !fold.IsFolded; }), "Jump to child unfolds its parent");

                // A line without "- " continues the previous block instead of parenting it,
                // so it must never fabricate a folding (Logseq shows the arrow only for real children).
                editor.Text = "- A\nB"; Invoke(editor, "RefreshFoldings");
                Require(!manager.AllFoldings.Any(), "Continuation line after a block creates no folding");
                editor.Text = "- A"; Invoke(editor, "RefreshFoldings");
                Require(!manager.AllFoldings.Any(), "Leaf block has no folding");
                editor.Text = "- A\n  - child"; Invoke(editor, "RefreshFoldings");
                Require(manager.AllFoldings.Count() == 1 && manager.AllFoldings.First().StartOffset == 3, "Block folds only its real child block");
                editor.Text = "- A\nB\n  - child"; Invoke(editor, "RefreshFoldings");
                Require(manager.AllFoldings.Count() == 1, "Continuation line stays inside the parent fold");

                editor.Text = original; editor.CaretOffset = 10; editor.ToggleFold();
                editor.MoveSelection(1);
                Require(manager.AllFoldings.Any(delegate(FoldingSection fold) { return fold.IsFolded && fold.StartOffset == 24; }), "Moving subtree preserves its collapsed state at new offset");
                editor.Undo(); Require(editor.Text == original && manager.AllFoldings.Any(delegate(FoldingSection fold) { return fold.IsFolded && fold.StartOffset == 16; }), "Move undo restores original collapsed subtree");
                editor.Redo(); Require(manager.AllFoldings.Any(delegate(FoldingSection fold) { return fold.IsFolded && fold.StartOffset == 24; }), "Move redo restores moved collapse state");
                editor.Text = original; editor.Select(10, 13); Pump(window);
                object drag = Invoke(editor, "CreateBlockDrag", 2);
                Require(drag != null && editor.TextArea.LeftMargins.Any(delegate(System.Windows.UIElement margin) { return margin.GetType().Name == "BlockGripMargin"; }), "Dedicated native block grip produces same-document drag payload");
                VisualLine lastVisual = editor.TextArea.TextView.GetVisualLine(4);
                Point dropPoint = new Point(8, lastVisual.VisualTop - editor.TextArea.TextView.VerticalOffset + lastVisual.TextLines[0].Height * 0.8);
                DataObject dragData = new DataObject("FlowLog.Native.OutlineBlock.v1", drag);
                ConstructorInfo dragConstructor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                DragEventArgs over = (DragEventArgs)dragConstructor.Invoke(new object[] { dragData, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, editor.TextArea.TextView, dropPoint });
                over.RoutedEvent = DragDrop.PreviewDragOverEvent;
                Invoke(editor, "HandleBlockDragOver", editor, over);
                Require(over.Handled && over.Effects == DragDropEffects.Move, "Native drag hit testing accepts sibling insertion");
                DragEventArgs drop = (DragEventArgs)dragConstructor.Invoke(new object[] { dragData, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, editor.TextArea.TextView, dropPoint });
                drop.RoutedEvent = DragDrop.PreviewDropEvent;
                Invoke(editor, "HandleBlockDrop", editor, drop);
                Require(drop.Handled && drop.Effects == DragDropEffects.Move && editor.Text == "- first\n- third\n- second\n  - child", "Native drop moves entire selected subtree");
                Require(editor.SelectionStart == 18 && editor.SelectionLength == 13, "Native subtree drag keeps text selection");
                editor.Undo(); Require(editor.Text == original && editor.SelectionStart == 10 && editor.SelectionLength == 13, "Native drag is one undo with restored selection");
                editor.Redo(); Require(editor.Text == "- first\n- third\n- second\n  - child", "Native drag redo exact source");
                // A document edited during an OLE drag must never use stale offsets.
                editor.Text = original; editor.CaretOffset = 10; Pump(window); drag = Invoke(editor, "CreateBlockDrag", 2);
                dragData = new DataObject("FlowLog.Native.OutlineBlock.v1", drag); editor.Document.Insert(0, "- new\n");
                drop = (DragEventArgs)dragConstructor.Invoke(new object[] { dragData, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, editor.TextArea.TextView, dropPoint });
                drop.RoutedEvent = DragDrop.PreviewDropEvent;
                Invoke(editor, "HandleBlockDrop", editor, drop);
                Require(drop.Effects == DragDropEffects.None && editor.Text == "- new\n" + original, "Reject drag after document changed");
                editor.Text = original; editor.SourceMode = true;
                Require(Invoke(editor, "CreateBlockDrag", 2) == null, "Source mode does not start outline drag"); editor.SourceMode = false;

                editor.Text = "- [["; editor.CaretOffset = 4; editor.PageTitles = new[] { "中文页面" }; Entered(editor, "[");
                CompletionWindow completion = (CompletionWindow)typeof(OutlineEditor).GetField("completion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
                Require(completion != null && completion.CompletionList.CompletionData.Count == 1, "Page links use native completion window");
                completion.CompletionList.CompletionData[0].Complete(editor.TextArea, new TextSegment { StartOffset = 4, Length = 0 }, EventArgs.Empty); completion.Close();
                Require(editor.Text == "- [[中文页面]]", "Native link completion inserts Unicode title and delimiter");
                editor.Undo(); Require(editor.Text == "- [[", "Link completion atomic undo");
                editor.Text = "- /"; editor.CaretOffset = 3; Entered(editor, "/");
                completion = (CompletionWindow)typeof(OutlineEditor).GetField("completion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
                Require(completion != null && completion.CompletionList.CompletionData.Count == 6, "Slash command native completion list");
                completion.CompletionList.CompletionData[0].Complete(editor.TextArea, new TextSegment { StartOffset = 3, Length = 0 }, EventArgs.Empty); completion.Close();
                Require(editor.Text == "- TODO ", "Slash command removes trigger and applies task prefix");
                editor.Undo(); Require(editor.Text == "- /", "Slash completion and nested block operation are one undo");

                editor.Text = "- 中文甲\n- 中文乙"; editor.Select(4, 5);
                editor.TextArea.Selection.ReplaceSelectionWithText("粘贴");
                Require(editor.Text == "- 中文粘贴文乙", "Native replacement supports cross-block selection");
                editor.Undo(); Require(editor.Text == "- 中文甲\n- 中文乙", "Native cross-block replace uses Avalon undo stack");
                editor.SourceMode = true; Pump(window); editor.SourceMode = false; Pump(window);
                Require(editor.Text == "- 中文甲\n- 中文乙", "Reader/source mode switching is lossless");

                Require(Outline.BoldPattern.Match("**ABC**").Success && Outline.BoldPattern.Match("**ABC**").Groups[1].Value == "ABC", "BoldPattern matches **ABC** and captures ABC");
                Require(!Outline.BoldPattern.Match("*ABC*").Success, "BoldPattern rejects single-asterisk emphasis");
                editor.Text = "- **ABC**";
                Pump(window);
                VisualLine boldVisual = editor.TextArea.TextView.VisualLines.First();
                Require(boldVisual.Elements.Any(delegate(VisualLineElement element)
                {
                    return element.TextRunProperties.Typeface.Weight == FontWeights.Bold;
                }), "Inline bold **ABC** renders with bold weight");
                Require(Outline.ItalicPattern.Match("*ABC*").Success && Outline.ItalicPattern.Match("*ABC*").Groups[1].Value == "ABC", "ItalicPattern matches *ABC* and captures ABC");
                Require(!Outline.ItalicPattern.Match("**ABC**").Success, "ItalicPattern rejects bold **ABC**");
                editor.Text = "- *ABC*";
                Pump(window);
                VisualLine italicVisual = editor.TextArea.TextView.VisualLines.First();
                Require(italicVisual.Elements.Any(delegate(VisualLineElement element)
                {
                    return element.TextRunProperties.Typeface.Style == FontStyles.Italic;
                }), "Inline italic *ABC* renders with italic style");

                Require(Outline.StrikethroughPattern.Match("~~ABC~~").Success && Outline.StrikethroughPattern.Match("~~ABC~~").Groups[1].Value == "ABC", "StrikethroughPattern matches ~~ABC~~ and captures ABC");
                Require(!Outline.StrikethroughPattern.Match("~ABC~").Success, "StrikethroughPattern rejects single-tilde emphasis");
                editor.Text = "- ~~ABC~~";
                Pump(window);
                VisualLine strikeVisual = editor.TextArea.TextView.VisualLines.First();
                Require(strikeVisual.Elements.Any(delegate(VisualLineElement element)
                {
                    return element.TextRunProperties.TextDecorations == TextDecorations.Strikethrough;
                }), "Inline strikethrough ~~ABC~~ renders with strikethrough decoration");

                Require(Outline.UnderlinePattern.Match("_ABC_").Success && Outline.UnderlinePattern.Match("_ABC_").Groups[1].Value == "ABC", "UnderlinePattern matches _ABC_ and captures ABC");
                Require(!Outline.UnderlinePattern.Match("__ABC__").Success, "UnderlinePattern rejects double-underscore emphasis");
                editor.Text = "- _ABC_";
                Pump(window);
                VisualLine underlineVisual = editor.TextArea.TextView.VisualLines.First();
                Require(underlineVisual.Elements.Any(delegate(VisualLineElement element)
                {
                    return element.TextRunProperties.TextDecorations == TextDecorations.Underline;
                }), "Inline underline _ABC_ renders with underline decoration");

                Require(Outline.HighlightPattern.Match("==ABC==").Success && Outline.HighlightPattern.Match("==ABC==").Groups[1].Value == "ABC", "HighlightPattern matches ==ABC== and captures ABC");
                Require(!Outline.HighlightPattern.Match("===ABC===").Success, "HighlightPattern rejects triple-equals emphasis");
                editor.Text = "- ==ABC==";
                Pump(window);
                VisualLine highlightVisual = editor.TextArea.TextView.VisualLines.First();
                Require(highlightVisual.Elements.Any(delegate(VisualLineElement element)
                {
                    var props = element.TextRunProperties;
                    var bgField = props.GetType().GetField("backgroundBrush", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (bgField == null) return false;
                    var brush = bgField.GetValue(props) as SolidColorBrush;
                    return brush != null && brush.Color == Colors.Yellow;
                }), "Inline highlight ==ABC== renders with yellow background");
                Require(Outline.CodePattern.Match("`ABC`").Success && Outline.CodePattern.Match("`ABC`").Groups[1].Value == "ABC", "CodePattern matches `ABC` and captures ABC");
                Require(!Outline.CodePattern.Match("`").Success, "CodePattern rejects lone backtick");
                editor.Text = "- `ABC`";
                Pump(window);
                VisualLine codeVisual = editor.TextArea.TextView.VisualLines.First();
                Require(codeVisual.Elements.Any(delegate(VisualLineElement element)
                {
                    var props = element.TextRunProperties;
                    var bgField = props.GetType().GetField("backgroundBrush", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (bgField == null) return false;
                    var brush = bgField.GetValue(props) as SolidColorBrush;
                    return brush != null && brush.Color == Color.FromRgb(242, 242, 242);
                }), "Inline code `ABC` renders with gray background");

                const string literal = "- `x -> <- @P @D @T @L **bold** *italic* ~~strike~~ _under_ ==mark== [[page]]` -> @P **outside**";
                editor.Text = literal; Pump(window);
                Require(editor.TextArea.TextView.VisualLines.First().Elements.Count(x => x is FormattedTextElement) == 3, "Only the bullet and symbols outside inline code are replaced");
                foreach (string token in new [] { "->", "<-", "@P", "@D", "@T", "@L", "bold", "italic", "strike", "under", "mark", "page" })
                {
                    VisualLineElement element = ElementAt(editor, literal.IndexOf(token, StringComparison.Ordinal));
                    Require(element is VisualLineText && element.TextRunProperties.Typeface.Weight == FontWeights.Normal
                        && element.TextRunProperties.Typeface.Style == FontStyles.Normal
                        && (element.TextRunProperties.TextDecorations == null || element.TextRunProperties.TextDecorations.Count == 0)
                        && ((SolidColorBrush)element.TextRunProperties.BackgroundBrush).Color == Color.FromRgb(242, 242, 242), "Inline code keeps literal text and excludes markup: " + token);
                }
                Require(ElementAt(editor, literal.IndexOf("outside", StringComparison.Ordinal)).TextRunProperties.Typeface.Weight == FontWeights.Bold, "Emphasis outside inline code still renders");
                Require(editor.Text == literal, "Inline code rendering preserves the source");
                editor.Text = "- `->` @D `@L **literal**` <-"; Pump(window);
                Require(editor.TextArea.TextView.VisualLines.First().Elements.Count(x => x is FormattedTextElement) == 3, "Multiple code spans exclude symbols while surrounding mentions and arrows render");

                foreach (bool dark in new [] { false, true })
                {
                    editor.Background = new SolidColorBrush(dark ? Color.FromRgb(0, 43, 54) : Colors.White);
                    editor.Foreground = new SolidColorBrush(dark ? Color.FromRgb(164, 181, 182) : Color.FromRgb(67, 63, 56));
                    editor.Text = "- `code` ==highlight== plain"; Pump(window);
                    foreach (string token in new [] { "code", "highlight" })
                    {
                        var props = ElementAt(editor, editor.Text.IndexOf(token, StringComparison.Ordinal)).TextRunProperties;
                        Require(Contrast(props.ForegroundBrush, props.BackgroundBrush) >= 4.5, "Inline " + token + " has readable contrast in " + (dark ? "dark" : "light") + " theme");
                    }
                    Require(ElementAt(editor, editor.Text.IndexOf("plain", StringComparison.Ordinal)).TextRunProperties.ForegroundBrush == editor.Foreground, "Inline colors do not leak into surrounding text");
                }
                return count;
            }
            finally { window.Close(); }
        }
    }
}
