using System;

namespace MusicSyncConverter
{
    class OutputFile
    {
        public string Path { get; set; } // E:/Audio/Music/Test.mp3
        public DateTime ModifiedDate { get; set; }
        public byte[] Content { get; set; }
    }
}
