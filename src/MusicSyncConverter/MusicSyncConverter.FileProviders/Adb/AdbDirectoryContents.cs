using Microsoft.Extensions.FileProviders;
using MusicSyncConverter.AdbAbstraction;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbDirectoryContents : IDirectoryContents
    {
        private readonly string _path;
        private readonly IEnumerable<StatEntry> _dirList;
        private readonly AdbSyncClient _syncService;

        public AdbDirectoryContents(string path, IEnumerable<StatEntry> dirList, AdbSyncClient syncService)
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
                if (item.Mode.HasFlag(UnixFileMode.RegularFile) || item.Mode.HasFlag(UnixFileMode.Directory))
                    yield return new AdbFileInfo(_path, item, _syncService);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
