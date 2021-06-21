using System;

namespace MusicSyncConverter
{
    internal class SourceFile
    {
        public string Path { get; internal set; }
        public DateTime ModifiedDate { get; internal set; }
    }
}