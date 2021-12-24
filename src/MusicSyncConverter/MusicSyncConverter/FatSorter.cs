using MusicSyncConverter.Config;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MusicSyncConverter
{
    public class FatSorter
    {
        public void Sort(string path, FatSortMode sortMode, bool recurse, CancellationToken cancellationToken)
        {
            if (path == null || !Directory.Exists(path) || sortMode == FatSortMode.None)
            {
                return;
            }

            SortInternal(new DirectoryInfo(path), sortMode, recurse, cancellationToken);
        }

        private void SortInternal(DirectoryInfo directory, FatSortMode sortMode, bool recurse, CancellationToken cancellationToken)
        {
            var entries = directory.GetFileSystemInfos();

            if (recurse)
            {
                foreach (var subdir in entries.OfType<DirectoryInfo>())
                {
                    SortInternal(subdir, sortMode, recurse, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Length <= 1)
                return;
            if (sortMode == FatSortMode.Folders && entries.OfType<DirectoryInfo>().Count() <= 1)
                return;
            if (sortMode == FatSortMode.Files && entries.OfType<FileInfo>().Count() <= 1)
                return;

            Console.WriteLine($"Sorting {directory.FullName}");

            var tmpDirName = Path.Combine(directory.FullName, "MusicSyncConverter.FatSorter.Temp");
            var tmpDir = Directory.CreateDirectory(tmpDirName);

            if (sortMode.HasFlag(FatSortMode.Folders))
            {
                foreach (var subdir in entries.OfType<DirectoryInfo>())
                {
                    subdir.MoveTo(Path.Combine(tmpDirName, subdir.Name));
                }

                foreach (var subdir in tmpDir.GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    subdir.MoveTo(Path.Combine(directory.FullName, subdir.Name));
                }
            }

            if (sortMode.HasFlag(FatSortMode.Files))
            {
                foreach (var file in entries.OfType<FileInfo>())
                {
                    file.MoveTo(Path.Combine(tmpDirName, file.Name));
                }

                foreach (var file in tmpDir.GetFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    file.MoveTo(Path.Combine(directory.FullName, file.Name));
                }
            }

            Directory.Delete(tmpDirName, false);
        }
    }
}
