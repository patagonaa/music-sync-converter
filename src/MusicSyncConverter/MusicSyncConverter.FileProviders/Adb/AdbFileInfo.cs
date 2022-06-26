using Microsoft.Extensions.FileProviders;
using SharpAdbClient;
using System;
using System.IO;
using System.Threading;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbFileInfo : IFileInfo
    {
        private readonly string _directory;
        private readonly FileStatistics _item;
        private readonly SyncService _syncService;
        private readonly SemaphoreSlim _syncServiceSemaphore;

        public AdbFileInfo(string directory, FileStatistics item, SyncService syncService, SemaphoreSlim syncServiceSemaphore)
        {
            _directory = directory;
            _item = item;
            _syncService = syncService;
            _syncServiceSemaphore = syncServiceSemaphore;
        }

        public bool Exists => _item.FileMode != 0;

        public long Length => _item.Size;

        public string? PhysicalPath => null;

        public string Name => _item.Path;

        public DateTimeOffset LastModified => _item.Time;

        public bool IsDirectory => _item.FileMode.HasFlag(UnixFileMode.Directory);

        public string FullPath => AdbSyncTarget.UnixizePath(Path.Join(_directory, _item.Path));

        public Stream CreateReadStream()
        {
            _syncServiceSemaphore.Wait();
            try
            {
                var ms = new MemoryStream(_item.Size);
                _syncService.Pull(FullPath, ms, null, CancellationToken.None);
                return ms;
            }
            finally
            {
                _syncServiceSemaphore.Release();
            }
        }
    }
}
