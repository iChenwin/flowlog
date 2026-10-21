using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FlowLog.Native
{
    public static class StorageTests
    {
        public static int Run()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlowLog-StorageTests-" + Guid.NewGuid().ToString("N"));
            int count = 0;
            VaultStore store = new VaultStore(root);
            Note draftNote = VaultStore.CreatePage("未保存草稿-" + Guid.NewGuid().ToString("N"));
            try
            {
                Note note = VaultStore.CreateJournal("2026-10-21");
                string original = "原始正文\r\n没有强制大纲\r\n";
                note.HasBom = true;
                string revision = store.Save(note, original, "");
                Require(revision.Length == 64 && File.Exists(System.IO.Path.Combine(root, note.Path)), "Create and hash"); count++;
                byte[] before = File.ReadAllBytes(System.IO.Path.Combine(root, note.Path));
                Note loaded = store.ListNotes().Single();
                Require(loaded.Content == original && loaded.HasBom && before[0] == 239, "BOM and CRLF round trip"); count++;
                store.Save(loaded, loaded.Content, loaded.Revision);
                Require(before.SequenceEqual(File.ReadAllBytes(System.IO.Path.Combine(root, note.Path))) && !Directory.Exists(System.IO.Path.Combine(root, ".flowlog", "backups")), "No-op does not modify or back up"); count++;
                store.Save(loaded, original + "new", loaded.Revision);
                Require(Directory.GetFiles(System.IO.Path.Combine(root, ".flowlog", "backups"), "*.md", SearchOption.AllDirectories).Length == 1, "Backup retains exact previous bytes"); count++;
                bool conflict = false;
                try { store.Save(loaded, "stale writer", revision); } catch (IOException) { conflict = true; }
                Require(conflict && Directory.GetFiles(System.IO.Path.Combine(root, ".flowlog", "conflicts")).Length == 2 && store.ListNotes().Single().Content.EndsWith("new"), "Concurrent edit retains both versions"); count++;
                draftNote.Content = "尚未保存\r\n- 草稿";
                store.SaveDraft(draftNote, "");
                Draft draft = store.ListDrafts().Single(x => x.Note.Path == draftNote.Path);
                Require(draft.Note.Content == draftNote.Content && !draft.Conflict && !File.Exists(System.IO.Path.Combine(root, draftNote.Path)), "Recover unsaved draft"); count++;
                store.Save(draftNote, "external content", "");
                Require(store.ListDrafts().Single(x => x.Note.Path == draftNote.Path).Conflict, "Recovered draft detects changed disk"); count++;
                store.ClearDraft(draftNote.Path);
                Require(!store.ListDrafts().Any(), "Explicit draft clear"); count++;
                bool rejected = false;
                try { store.Save(new Note { Path = "pages/../../outside.md" }, "bad", ""); } catch (IOException) { rejected = true; }
                Require(rejected, "Reject path traversal"); count++;
                rejected = false;
                try { store.Save(new Note { Path = "pages/code.exe" }, "bad", ""); } catch (IOException) { rejected = true; }
                Require(rejected, "Reject uncontrolled extensions"); count++;
                Note page = VaultStore.CreatePage("中文 / 任务？");
                store.Save(page, "Unicode", "");
                Require(store.ListNotes().Any(x => x.Title == "中文 / 任务？"), "Percent-encoded Unicode page title"); count++;
                foreach (string title in new [] { "tail.", "tail..", "CON", "..", "slash/name", "emoji-\ud83d\ude80" })
                {
                    Note special = VaultStore.CreatePage(title);
                    store.Save(special, "Title: " + title, "");
                    Require(store.ListNotes().Any(x => x.Path == special.Path && x.Title == title), "Round trip special page title: " + title); count++;
                }
                Require(VaultStore.CreatePage("tail.").Path != VaultStore.CreatePage("tail").Path, "Trailing dot page has distinct path"); count++;
                for (int i = 0; i < 24; i++) store.Save(loaded, "history " + i, loaded.Revision);
                Require(Directory.GetFiles(System.IO.Path.Combine(root, ".flowlog", "backups"), "*.md", SearchOption.AllDirectories).Length == 20, "Limit backup history to twenty per note"); count++;
                Note longTitle = VaultStore.CreatePage(new String('\u957f', 12));
                string longRevision = store.Save(longTitle, "long title original", "");
                store.Save(longTitle, "long title edited", longRevision);
                Require(store.ListNotes().Any(x => x.Path == longTitle.Path && x.Content == "long title edited"), "Encoded long title supports atomic writes and readable backups under Framework path limits"); count++;
                conflict = false;
                try { store.Save(longTitle, "long title conflict", longRevision); } catch (IOException) { conflict = true; }
                Require(conflict && Directory.GetFiles(System.IO.Path.Combine(root, ".flowlog", "conflicts"), "*.local.md").Any(x => File.ReadAllText(x).Contains("long title conflict")), "Encoded long title supports conflict copies under Framework path limits"); count++;
                draftNote.Content = "Save succeeded before draft cleanup\r\n";
                store.SaveDraft(draftNote, draftNote.Revision);
                store.Save(draftNote, draftNote.Content, draftNote.Revision);
                Draft savedDraft = store.ListDrafts().Single(x => x.Note.Path == draftNote.Path);
                Require(!savedDraft.Conflict && savedDraft.BaseRevision == draftNote.Revision && savedDraft.Note.Revision == draftNote.Revision, "Already-saved draft recovers without a false conflict"); count++;
                store.ClearDraft(draftNote.Path);
                string invalidFile = System.IO.Path.Combine(root, "pages", "invalid.md");
                File.WriteAllBytes(invalidFile, new byte[] { 255, 254, 0 });
                rejected = false;
                try { store.ListNotes(); } catch (DecoderFallbackException) { rejected = true; }
                Require(rejected && File.ReadAllBytes(invalidFile)[0] == 255, "Reject invalid UTF-8 without data loss"); count++;
                return count;
            }
            finally
            {
                store.ClearDraft(draftNote.Path);
                if (root.StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlowLog-StorageTests-"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        private static void Require(bool condition, string description) { if (!condition) throw new Exception("Storage test failed: " + description); }
    }
}
