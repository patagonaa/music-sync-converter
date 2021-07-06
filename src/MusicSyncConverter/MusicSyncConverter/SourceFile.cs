using System;

namespace MusicSyncConverter
{
    internal class SourceFile
    {
        public string Path { get; internal set; } // Audio/Music/Test.mp3
        public DateTime ModifiedDate { get; internal set; }
    }
}