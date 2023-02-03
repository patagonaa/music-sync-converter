using System;

namespace MusicSyncConverter.FileProviders.SyncTargets
{
    public class SyncTargetFileInfo
    {
        public SyncTargetFileInfo(string path, string name, bool isDirectory, DateTimeOffset lastModified)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            IsDirectory = isDirectory;
            LastModified = lastModified;
        }
        public string Path { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public DateTimeOffset LastModified { get; }
    }
}
