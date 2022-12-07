using AdbClient;
using Microsoft.Extensions.FileProviders;
using System;
using System.IO;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbFileInfo : IFileInfo
    {
        private readonly string _directory;
        private readonly StatEntry _item;
        private readonly AdbSyncClient _syncService;

        public AdbFileInfo(string directory, StatEntry item, AdbSyncClient syncService)
        {
            _directory = directory;
            _item = item;
            _syncService = syncService;
        }

        public bool Exists => _item.Mode != 0;

        public long Length => _item.Size;

        public string? PhysicalPath => null;

        public string Name => _item.Path;

        public DateTimeOffset LastModified => _item.ModifiedTime;

        public bool IsDirectory => _item.Mode.HasFlag(UnixFileMode.Directory);

        public string FullPath => AdbSyncTarget.UnixizePath(Path.Join(_directory, _item.Path));

        public Stream CreateReadStream()
        {
            var ms = new MemoryStream(checked((int)_item.Size));
            _syncService.Pull(FullPath, ms).Wait();
            return ms;
        }
    }
}
