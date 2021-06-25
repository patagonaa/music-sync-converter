using System;

namespace MusicSyncConverter
{
    class OutputFile
    {
        public string Path { get; set; }
        public DateTime ModifiedDate { get; set; }
        public byte[] Content { get; set; }
    }
}
