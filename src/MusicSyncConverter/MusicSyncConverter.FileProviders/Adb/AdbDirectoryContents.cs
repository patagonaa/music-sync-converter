using Microsoft.Extensions.FileProviders;
using SharpAdbClient;
using System.Collections;
using System.Collections.Generic;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbDirectoryContents : IDirectoryContents
    {
        private readonly string _path;
        private readonly IEnumerable<FileStatistics> _dirList;
        private readonly SyncService _syncService;

        public AdbDirectoryContents(string path, IEnumerable<FileStatistics> dirList, SyncService syncService)
        {
            _path = path;
            _dirList = dirList;
            _syncService = syncService;
        }

        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator()
        {
            foreach (var item in _dirList)
            {
                if (item.FileMode.HasFlag(UnixFileMode.Regular) || item.FileMode.HasFlag(UnixFileMode.Directory))
                    yield return new AdbFileInfo(_path, item, _syncService);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
