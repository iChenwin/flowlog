using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FlowLog.Native
{
    // Offsets always refer to the unmodified Markdown document. Rendering never
    // rewrites the source, so native selection, clipboard and undo keep working.
    public sealed class OutlineLine
    {
        public int Number, Offset, Length, DelimiterLength;
        public string Text;
        public int EndOffset { get { return Offset + Length; } }
        public int TotalLength { get { return Length + DelimiterLength; } }
    }

    public sealed class OutlineBlock
    {
        public int Line, LastLine, Offset, EndOffset, Indent, PrefixLength;
        public string Whitespace, Content;
        public int ContentOffset { get { return Offset + PrefixLength; } }
    }

    public sealed class OutlineRange
    {
        public int Start, End, FirstLine, LastLine;
        public int Length { get { return End - Start; } }
    }

    public static class Outline
    {
        public static readonly Regex BlockPattern = new Regex(@"^([ \t]*)- (.*)$", RegexOptions.Compiled);
        public static readonly Regex MetadataPattern = new Regex(@"^\s*[\w-]+::(?:\s.*)?$", RegexOptions.Compiled);
        public static readonly Regex LinkPattern = new Regex(@"\[\[([^\]\r\n]+)\]\]|\(\(([0-9a-fA-F-]{36})\)\)|(?<![\w/#])#([^\s#\[\](),.;:!?]+)", RegexOptions.Compiled);
        public static readonly Regex IdPattern = new Regex(@"^\s*id::\s*([0-9a-fA-F-]{36})\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        public static List<OutlineLine> Lines(string text)
        {
            text = text ?? "";
            List<OutlineLine> result = new List<OutlineLine>();
            int start = 0, number = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\r' && text[i] != '\n') continue;
                int delimiter = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                result.Add(new OutlineLine { Number = number++, Offset = start, Length = i - start,
                    DelimiterLength = delimiter, Text = text.Substring(start, i - start) });
                i += delimiter - 1;
                start = i + 1;
            }
            result.Add(new OutlineLine { Number = number, Offset = start, Length = text.Length - start,
                DelimiterLength = 0, Text = text.Substring(start) });
            return result;
        }

        public static int Indentation(string whitespace)
        {
            int count = 0;
            foreach (char c in whitespace) count += c == '\t' ? 2 : 1;
            return count;
        }

        public static List<OutlineBlock> Parse(string text)
        {
            List<OutlineLine> lines = Lines(text);
            List<OutlineBlock> blocks = new List<OutlineBlock>();
            HashSet<int> codeLines = CodeLines(text);
            foreach (OutlineLine line in lines)
            {
                if (codeLines.Contains(line.Number)) continue;
                Match match = BlockPattern.Match(line.Text);
                if (!match.Success) continue;
                string whitespace = match.Groups[1].Value;
                blocks.Add(new OutlineBlock { Line = line.Number, LastLine = lines.Count, Offset = line.Offset,
                    EndOffset = (text ?? "").Length, Indent = Indentation(whitespace),
                    Whitespace = whitespace, PrefixLength = whitespace.Length + 2, Content = match.Groups[2].Value });
            }
            // A stack makes subtree discovery linear even for deeply nested files.
            Stack<OutlineBlock> stack = new Stack<OutlineBlock>();
            foreach (OutlineBlock block in blocks)
            {
                while (stack.Count > 0 && stack.Peek().Indent >= block.Indent)
                {
                    OutlineBlock previous = stack.Pop();
                    previous.EndOffset = block.Offset;
                    previous.LastLine = block.Line - 1;
                }
                stack.Push(block);
            }
            return blocks;
        }

        // Includes the code and closing fence, but excludes an opening fence
        // that is itself an outline block ("- ```csharp").
        public static HashSet<int> CodeLines(string text)
        {
            HashSet<int> result = new HashSet<int>();
            char fence = '\0';
            int count = 0;
            foreach (OutlineLine line in Lines(text))
            {
                string candidate = line.Text.TrimStart(' ', '\t');
                if (fence != '\0')
                {
                    result.Add(line.Number);
                    int run = 0;
                    while (run < candidate.Length && candidate[run] == fence) run++;
                    if (run >= count && String.IsNullOrWhiteSpace(candidate.Substring(run))) { fence = '\0'; count = 0; }
                    continue;
                }
                Match block = BlockPattern.Match(line.Text);
                if (block.Success) candidate = block.Groups[2].Value.TrimStart();
                if (candidate.Length < 3 || (candidate[0] != '`' && candidate[0] != '~')) continue;
                char marker = candidate[0];
                int markerLength = 0;
                while (markerLength < candidate.Length && candidate[markerLength] == marker) markerLength++;
                if (markerLength < 3) continue;
                fence = marker;
                count = markerLength;
                if (!block.Success) result.Add(line.Number);
            }
            return result;
        }

        public static OutlineBlock At(string text, int offset)
        {
            OutlineBlock result = null;
            foreach (OutlineBlock block in Parse(text))
            {
                if (block.Offset > offset) break;
                result = block;
            }
            return result;
        }

        public static OutlineRange Selection(string text, int start, int length, bool includeChildren)
        {
            List<OutlineLine> lines = Lines(text);
            int lastOffset = length > 0 ? start + length - 1 : start;
            OutlineBlock first = At(text, start), last = At(text, lastOffset);
            int a = first == null ? Math.Min(start, text.Length) : first.Offset;
            int b = last == null ? Math.Min(start + length, text.Length) : last.EndOffset;
            if (!includeChildren)
            {
                foreach (OutlineLine line in lines)
                {
                    if (line.Offset <= start && start <= line.EndOffset) a = line.Offset;
                    if (line.Offset <= lastOffset && lastOffset <= line.EndOffset) b = line.Offset + line.TotalLength;
                }
            }
            if (b < a) b = a;
            int firstLine = 1, lastLine = lines.Count;
            foreach (OutlineLine line in lines)
            {
                if (line.Offset <= a) firstLine = line.Number;
                if (line.Offset < b || (a == b && line.Offset == a)) lastLine = line.Number;
            }
            return new OutlineRange { Start = a, End = b, FirstLine = firstLine, LastLine = lastLine };
        }

        public static string Newline(string text)
        {
            return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        public static string RemoveIndent(string line)
        {
            if (line.StartsWith("\t", StringComparison.Ordinal)) return line.Substring(1);
            if (line.StartsWith("  ", StringComparison.Ordinal)) return line.Substring(2);
            if (line.StartsWith(" ", StringComparison.Ordinal)) return line.Substring(1);
            return line;
        }
    }
}
