using System;

namespace MusicSyncConverter.Models
{
    public class SourceFile
    {
        public string AbsolutePath { get; internal set; } // C:/Stuff/Audio/Music/Test.mp3
        public string RelativePath { get; internal set; } // Audio/Music/Test.mp3
        public DateTime ModifiedDate { get; internal set; }
    }
}