using System;

namespace MusicSyncConverter.FileProviders.SyncTargets
{
    public class SyncTargetFileInfo
    {
        public SyncTargetFileInfo(string path, bool isDirectory, DateTimeOffset lastModified)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Name = System.IO.Path.GetFileName(path);
            IsDirectory = isDirectory;
            LastModified = lastModified;
        }
        public string Path { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public DateTimeOffset LastModified { get; }
    }
}
