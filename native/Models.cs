using System;

namespace FlowLog.Native
{
    public sealed class Note
    {
        public string Path { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public string Revision { get; set; }
        public string Date { get; set; }
        public bool IsJournal { get; set; }
        public bool HasBom { get; set; }
    }

    public sealed class Draft
    {
        public Note Note { get; set; }
        public string BaseRevision { get; set; }
        public bool Conflict { get; set; }
    }
}
