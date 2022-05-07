using System;

namespace MusicSyncConverter
{
    class OutputFile
    {
        public string Path { get; set; } = null!; // Music/Test.mp3
        public DateTimeOffset ModifiedDate { get; set; }
        public string TempFilePath { get; set; } = null!;
    }
}
