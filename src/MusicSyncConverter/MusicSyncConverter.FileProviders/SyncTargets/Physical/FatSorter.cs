using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MusicSyncConverter.FileProviders.SyncTargets.Physical
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
            var tmpDir = DoWithRetries(() => Directory.CreateDirectory(tmpDirName), cancellationToken);

            if (sortMode.HasFlag(FatSortMode.Folders))
            {
                foreach (var subdir in entries.OfType<DirectoryInfo>())
                {
                    DoWithRetries(() => subdir.MoveTo(Path.Combine(tmpDirName, subdir.Name)), cancellationToken);
                }

                foreach (var subdir in tmpDir.GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    DoWithRetries(() => subdir.MoveTo(Path.Combine(directory.FullName, subdir.Name)), cancellationToken);
                }
            }

            if (sortMode.HasFlag(FatSortMode.Files))
            {
                foreach (var file in entries.OfType<FileInfo>())
                {
                    DoWithRetries(() => file.MoveTo(Path.Combine(tmpDirName, file.Name)), cancellationToken);
                }

                foreach (var file in tmpDir.GetFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    DoWithRetries(() => file.MoveTo(Path.Combine(directory.FullName, file.Name)), cancellationToken);
                }
            }

            Directory.Delete(tmpDirName, false);
        }

        private void DoWithRetries(Action action, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    action();
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
                Thread.Sleep(1000);
            }
        }

        private T DoWithRetries<T>(Func<T> action, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
                Thread.Sleep(1000);
            }
        }
    }
}
