using System;

namespace MusicSyncConverter.FileProviders.SyncTargets.Physical
{
    [Flags]
    public enum FatSortMode
    {
        None = 0,
        Files = 1 << 0,
        Folders = 1 << 1,
        FilesAndFolders = Files | Folders
    }
}