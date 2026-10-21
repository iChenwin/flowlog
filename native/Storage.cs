using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Globalization;
using System.Xml.Linq;
using System.Threading;

namespace FlowLog.Native
{
    public sealed class VaultStore
    {
        private static readonly Dictionary<string, object> Locks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly object gate;
        private readonly string mutexName;
        public string Root { get; private set; }
        private string DraftRoot { get { return System.IO.Path.Combine(AppRoot(), "drafts", Hash(Utf8.GetBytes(Root.ToUpperInvariant()))); } }

        public VaultStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("请选择笔记库目录。");
            Root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            mutexName = "Local\\FlowLogNative-Vault-" + Hash(Utf8.GetBytes(Root.ToUpperInvariant()));
            CheckReparse(Root);
            Directory.CreateDirectory(Root);
            CheckReparse(Root);
            lock (Locks)
            {
                if (!Locks.TryGetValue(Root, out gate)) { gate = new object(); Locks.Add(Root, gate); }
            }
        }

        public static string DefaultRoot() { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FlowLog"); }
        private static string AppRoot() { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowLogNative"); }
        public static string LoadLastRoot()
        {
            string file = System.IO.Path.Combine(AppRoot(), "last-vault.txt");
            try { return File.Exists(file) ? File.ReadAllText(file, Utf8).Trim() : DefaultRoot(); }
            catch (IOException) { return DefaultRoot(); }
            catch (UnauthorizedAccessException) { return DefaultRoot(); }
        }
        public static void RememberRoot(string root)
        {
            Directory.CreateDirectory(AppRoot());
            AtomicWrite(System.IO.Path.Combine(AppRoot(), "last-vault.txt"), Utf8.GetBytes(System.IO.Path.GetFullPath(root)));
        }
        public static Note CreateJournal(string date)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) throw new ArgumentException("日记日期格式应为 yyyy-MM-dd。");
            return new Note { Path = "journals/" + parsed.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture) + ".md", Title = date, Date = date, Content = "", Revision = "", IsJournal = true };
        }
        public static Note CreatePage(string title)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0) throw new ArgumentException("页面标题不能为空。");
            string encoded = Uri.EscapeDataString(title);
            string stem = title.Split('.')[0].ToUpperInvariant();
            if (new [] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) encoded = "%" + ((int)encoded[0]).ToString("X2") + encoded.Substring(1);
            int trailingDots = encoded.Length - encoded.TrimEnd('.').Length;
            if (trailingDots > 0) encoded = encoded.Substring(0, encoded.Length - trailingDots) + String.Concat(Enumerable.Repeat("%2E", trailingDots));
            if (encoded.Length == 0 || encoded == "." || encoded == "..") throw new ArgumentException("页面标题无效。");
            return new Note { Path = "pages/" + encoded + ".md", Title = title, Date = "", Content = "", Revision = "", IsJournal = false };
        }

        public List<Note> ListNotes()
        {
            lock (gate)
            using (new VaultLease(mutexName))
            {
                List<Note> notes = new List<Note>();
                foreach (string folder in new [] { "journals", "pages" })
                {
                    string dir = System.IO.Path.Combine(Root, folder);
                    CheckReparse(dir);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
                    {
                        string relative = folder + "/" + System.IO.Path.GetFileName(file);
                        string safe = NotePath(relative);
                        byte[] bytes = File.ReadAllBytes(safe);
                        bool bom = HasBom(bytes);
                        string content = Decode(bytes);
                        string stem = System.IO.Path.GetFileNameWithoutExtension(file);
                        DateTime date;
                        bool journal = folder == "journals" && DateTime.TryParseExact(stem, "yyyy_MM_dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
                        string iso = "";
                        if (journal) iso = DateTime.ParseExact(stem, "yyyy_MM_dd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        notes.Add(new Note { Path = relative, Title = journal ? iso : Uri.UnescapeDataString(stem), Content = content, Revision = Hash(bytes), Date = iso, IsJournal = journal, HasBom = bom });
                    }
                }
                return notes;
            }
        }

        public string Save(Note note, string content, string expectedRevision)
        {
            if (note == null) throw new ArgumentNullException("note");
            lock (gate)
            using (new VaultLease(mutexName))
            {
                string path = NotePath(note.Path);
                byte[] current = File.Exists(path) ? File.ReadAllBytes(path) : null;
                string revision = current == null ? "" : Hash(current);
                bool bom = current == null ? note.HasBom : HasBom(current);
                byte[] incoming = Encode(content ?? "", bom);
                if (!String.Equals(revision, expectedRevision ?? "", StringComparison.Ordinal))
                {
                    FailConflict(path, current, incoming);
                }
                if (current != null && current.SequenceEqual(incoming)) return revision;
                if (current != null) Backup(note.Path, current);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                NotePath(note.Path);
                byte[] latest = File.Exists(path) ? File.ReadAllBytes(path) : null;
                string latestRevision = latest == null ? "" : Hash(latest);
                if (!String.Equals(latestRevision, expectedRevision ?? "", StringComparison.Ordinal)) FailConflict(path, latest, incoming);
                AtomicWrite(path, incoming);
                note.Content = content ?? ""; note.Revision = Hash(incoming); note.HasBom = bom;
                return note.Revision;
            }
        }

        public void SaveDraft(Note note, string baseRevision)
        {
            lock (gate)
            using (new VaultLease(mutexName))
            {
                NotePath(note.Path);
                Directory.CreateDirectory(DraftRoot);
                XElement data = new XElement("draft", new XAttribute("path", note.Path), new XAttribute("title", note.Title ?? ""), new XAttribute("date", note.Date ?? ""), new XAttribute("journal", note.IsJournal), new XAttribute("bom", note.HasBom), new XAttribute("revision", baseRevision ?? ""), Convert.ToBase64String(Utf8.GetBytes(note.Content ?? "")));
                AtomicWrite(DraftFile(note.Path), Utf8.GetBytes(data.ToString(SaveOptions.DisableFormatting)));
            }
        }
        public List<Draft> ListDrafts()
        {
            lock (gate)
            using (new VaultLease(mutexName))
            {
                List<Draft> drafts = new List<Draft>();
                if (!Directory.Exists(DraftRoot)) return drafts;
                foreach (string file in Directory.GetFiles(DraftRoot, "*.xml"))
                {
                    XElement data = XElement.Load(file);
                    string path = (string)data.Attribute("path");
                    string full = NotePath(path);
                    string revision = (string)data.Attribute("revision") ?? "";
                    Note note = new Note { Path = path, Title = (string)data.Attribute("title"), Date = (string)data.Attribute("date"), IsJournal = (bool)data.Attribute("journal"), HasBom = (bool)data.Attribute("bom"), Revision = revision, Content = Utf8.GetString(Convert.FromBase64String(data.Value)) };
                    byte[] disk = File.Exists(full) ? File.ReadAllBytes(full) : null;
                    string actual = disk == null ? "" : Hash(disk);
                    if (disk != null && disk.SequenceEqual(Encode(note.Content, note.HasBom))) { revision = actual; note.Revision = actual; }
                    drafts.Add(new Draft { Note = note, BaseRevision = revision, Conflict = actual != revision });
                }
                return drafts;
            }
        }
        public void ClearDraft(string path) { lock (gate) using (new VaultLease(mutexName)) { NotePath(path); string file = DraftFile(path); if (File.Exists(file)) File.Delete(file); } }
        private sealed class VaultLease : IDisposable
        {
            private readonly Mutex mutex;
            public VaultLease(string name)
            {
                mutex = new Mutex(false, name);
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
            }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
        private string DraftFile(string path) { return System.IO.Path.Combine(DraftRoot, Hash(Utf8.GetBytes(path.Replace('\\', '/').ToUpperInvariant())) + ".xml"); }
        private void FailConflict(string path, byte[] current, byte[] incoming)
        {
            string conflicts = SafeInternal("conflicts");
            Directory.CreateDirectory(conflicts);
            string prefix = System.IO.Path.Combine(conflicts, HistoryName(path));
            if (current != null) AtomicWrite(prefix + ".disk.md", current);
            AtomicWrite(prefix + ".local.md", incoming);
            throw new IOException("文件已被其他程序修改。双方内容已保存在 .flowlog/conflicts，请手动合并。");
        }
        private void Backup(string path, byte[] bytes)
        {
            string dir = System.IO.Path.Combine(SafeInternal("backups"), Hash(Utf8.GetBytes(path.ToUpperInvariant())));
            CheckReparse(dir); Directory.CreateDirectory(dir); CheckReparse(dir);
            string file = HistoryName(path);
            AtomicWrite(System.IO.Path.Combine(dir, file), bytes);
            foreach (string old in Directory.GetFiles(dir).OrderByDescending(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal).Skip(20)) { CheckReparse(old); File.Delete(old); }
        }
        private string SafeInternal(string name) { string path = System.IO.Path.Combine(Root, ".flowlog", name); CheckReparse(path); return path; }
        private static string HistoryName(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length > 40) name = name.Substring(0, 40);
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + name + ".md";
        }
        private string NotePath(string relative)
        {
            if (String.IsNullOrEmpty(relative) || System.IO.Path.IsPathRooted(relative)) throw new IOException("非法笔记路径。");
            string[] parts = relative.Replace('\\', '/').Split('/');
            if (parts.Length != 2 || (parts[0] != "journals" && parts[0] != "pages") || parts[1] == "." || parts[1] == ".." || parts[1].IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || parts[1].EndsWith(" ") || !parts[1].EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new IOException("仅允许 journals 和 pages 下的 Markdown 文件。");
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));
            if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("笔记路径超出笔记库。");
            CheckReparse(full);
            return full;
        }
        private static void CheckReparse(string path)
        {
            string current = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            while (!String.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("为保护本地文件，笔记库不支持符号链接或目录联接。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = System.IO.Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }
        private static bool HasBom(byte[] bytes) { return bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191; }
        private static string Decode(byte[] bytes) { int offset = HasBom(bytes) ? 3 : 0; return Utf8.GetString(bytes, offset, bytes.Length - offset); }
        private static byte[] Encode(string content, bool bom) { byte[] body = Utf8.GetBytes(content); if (!bom) return body; return new byte[] {239,187,191}.Concat(body).ToArray(); }
        private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); } }
        private static void AtomicWrite(string path, byte[] bytes)
        {
            CheckReparse(path);
            string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), ".fl-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                CheckReparse(path);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
