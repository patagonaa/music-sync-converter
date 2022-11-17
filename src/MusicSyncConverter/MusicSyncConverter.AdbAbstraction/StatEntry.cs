using System;

namespace MusicSyncConverter.AdbAbstraction
{
    public class StatEntry
    {
        public string Path { get; internal set; }
        public UnixFileMode Mode { get; internal set; }
        public int Size { get; internal set; }
        public DateTime ModifiedTime { get; internal set; }

        public override string ToString()
        {
            return Path;
        }
    }
}
