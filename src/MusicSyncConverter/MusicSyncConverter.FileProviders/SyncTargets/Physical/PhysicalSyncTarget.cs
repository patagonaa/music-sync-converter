using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.SyncTargets.Physical
{
    class PhysicalSyncTarget : ISyncTarget
    {
        private readonly string _basePath;
        private readonly FatSortMode _sortMode;
        private readonly bool _isCaseSensitive;
        private readonly ConcurrentDictionary<NormalizedPath, object?> _updatedDirectories;
        private readonly FatSorter _fatSorter;

        public PhysicalSyncTarget(string basePath, FatSortMode sortMode)
        {
            _basePath = basePath;
            _sortMode = sortMode;
            _isCaseSensitive = IsCaseSensitiveInternal(basePath);
            _updatedDirectories = new ConcurrentDictionary<NormalizedPath, object?>(new PathComparer(_isCaseSensitive)); // there is no ConcurrentHashSet so we use the ConcurrentDictionary for that
            _fatSorter = new FatSorter();
        }

        public Task<SyncTargetFileInfo?> GetFileInfo(string path, CancellationToken cancellationToken = default) => Task.FromResult(GetFileInfoInternal(path));
        private SyncTargetFileInfo? GetFileInfoInternal(string path)
        {
            var physicalPath = GetPhysicalPath(path);

            var fileInfo = new FileInfo(physicalPath);
            if (fileInfo.Exists)
            {
                return new SyncTargetFileInfo(path, false, fileInfo.LastWriteTime);
            }

            var dirInfo = new DirectoryInfo(physicalPath);
            if (dirInfo.Exists)
            {
                return new SyncTargetFileInfo(path, true, dirInfo.LastWriteTime);
            }

            return null;
        }

        public Task<IList<SyncTargetFileInfo>?> GetDirectoryContents(string subpath, CancellationToken cancellationToken = default) => Task.FromResult(GetDirectoryContentsInternal(subpath, cancellationToken));
        private IList<SyncTargetFileInfo>? GetDirectoryContentsInternal(string subpath, CancellationToken cancellationToken = default)
        {
            var physicalPath = GetPhysicalPath(subpath);
            var dirInfo = new DirectoryInfo(physicalPath);
            if (!dirInfo.Exists)
                return null;
            var toReturn = new List<SyncTargetFileInfo>();
            foreach (var item in dirInfo.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                toReturn.Add(new SyncTargetFileInfo(Path.Join(subpath, item.Name), item is DirectoryInfo, item.LastWriteTime));
            }

            return toReturn;
        }

        public async Task WriteFile(string path, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            var absolutePath = GetPhysicalPath(path);

            var dirInfo = new DirectoryInfo(Path.GetDirectoryName(absolutePath)!);
            _updatedDirectories.TryAdd(new NormalizedPath(dirInfo.FullName), null);
            while (!dirInfo.Exists)
            {
                dirInfo = dirInfo.Parent;
                if (dirInfo == null)
                    throw new InvalidOperationException($"Parent should not be null! {absolutePath}");
                _updatedDirectories.TryAdd(new NormalizedPath(dirInfo.FullName), null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            File.Delete(absolutePath); // delete the file if it exists to allow for name case changes is on a case-insensitive filesystem
            using (var outputFile = new FileStream(absolutePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(outputFile, cancellationToken);
            }

            if (modified.HasValue)
                File.SetLastWriteTime(absolutePath, modified.Value.LocalDateTime);
        }

        private string GetPhysicalPath(string path)
        {
            return Path.Join(_basePath, path);
        }

        public Task<bool> IsCaseSensitive() => Task.FromResult(_isCaseSensitive);

        private bool IsCaseSensitiveInternal(string path)
        {
            var path1 = Path.Combine(path, "test.tmp");
            var path2 = Path.Combine(path, "TEST.tmp");
            File.Delete(path1);
            File.Delete(path2);
            using (File.Create(path1))
            {
            }

            var toReturn = !File.Exists(path2);

            File.Delete(path1);
            return toReturn;
        }

        public Task Delete(SyncTargetFileInfo file, CancellationToken cancellationToken)
        {
            var physicalPath = GetPhysicalPath(file.Path);
            if (file.IsDirectory)
            {
                Directory.Delete(physicalPath);
            }
            else
            {
                File.Delete(physicalPath);

                // if this happens, we most likely ran into a stupid Windows VFAT Unicode bug that points different filenames to the same or separate files depending on the operation.
                // https://twitter.com/patagona/status/1444626808935264261
                if (File.Exists(physicalPath))
                {
                    File.Delete(physicalPath);
                    Console.WriteLine($"Couldn't delete {physicalPath} on the first try. This is probably due to a Windows bug related to VFAT (FAT32) handling. Re-Run sync to fix this.");
                }
            }
            return Task.CompletedTask;
        }

        public Task Delete(IReadOnlyCollection<SyncTargetFileInfo> files, CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Delete(file, cancellationToken);
            }
            return Task.CompletedTask;
        }

        public Task Complete(CancellationToken cancellationToken)
        {
            foreach (var updatedDir in _updatedDirectories.Keys.OrderByDescending(x => x.Path.Length))
            {
                _fatSorter.Sort(updatedDir.Path, _sortMode, false, cancellationToken);
            }
            return Task.CompletedTask;
        }

        public Task<bool> IsHidden(string path) => Task.FromResult(IsHiddenInternal(path));
        private bool IsHiddenInternal(string path)
        {
            var pathStack = PathUtils.GetPathStack(path);
            if (pathStack.First().StartsWith('.'))
            {
                return true;
            }

            // if we're on Windows (or macOS?), also check Hidden attribute
            if (Environment.OSVersion.Platform != PlatformID.Unix)
            {
                var physicalPath = GetPhysicalPath(path);
                var attributes = File.GetAttributes(physicalPath);
                if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
                    return true;
            }

            return false;
        }
    }
}
