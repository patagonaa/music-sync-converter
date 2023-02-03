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

        public Task<SyncTargetFileInfo?> GetFileInfo(string path, CancellationToken cancellationToken = default)
        {
            var physicalPath = GetPhysicalPath(path);
            if (File.Exists(physicalPath))
            {
                var fileInfo = new FileInfo(physicalPath);
                return Task.FromResult<SyncTargetFileInfo?>(new SyncTargetFileInfo(path, fileInfo.Name, false, fileInfo.LastWriteTime));
            }
            else if (Directory.Exists(physicalPath))
            {
                var dirInfo = new DirectoryInfo(physicalPath);
                return Task.FromResult<SyncTargetFileInfo?>(new SyncTargetFileInfo(path, dirInfo.Name, true, dirInfo.LastWriteTime));
            }
            else
            {
                return Task.FromResult<SyncTargetFileInfo?>(null);
            }
        }

        public async Task<IList<SyncTargetFileInfo>?> GetDirectoryContents(string subpath, CancellationToken cancellationToken = default)
        {
            var physicalPath = GetPhysicalPath(subpath);
            if (!Directory.Exists(physicalPath))
                return null;
            var toReturn = new List<SyncTargetFileInfo>();
            foreach (var item in Directory.EnumerateFileSystemEntries(physicalPath))
            {
                var fileInfo = await GetFileInfo(Path.Join(subpath, Path.GetFileName(item)), cancellationToken);
                if (fileInfo != null)
                    toReturn.Add(fileInfo);
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
            using (var outputFile = File.OpenWrite(absolutePath))
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

        public Task<bool> IsHidden(string path, bool recurse) => Task.FromResult(IsHiddenInternal(path, recurse));
        private bool IsHiddenInternal(string path, bool recurse)
        {
            if (recurse)
            {
                foreach (var pathPart in PathUtils.GetPathStack(path).Reverse())
                {
                    if (pathPart.StartsWith('.'))
                        return true;

                    // if we're on Windows (or macOS?), also check Hidden attribute
                    if (Environment.OSVersion.Platform != PlatformID.Unix)
                    {
                        var physicalPath = GetPhysicalPath(pathPart);
                        var attributes = File.GetAttributes(physicalPath);
                        if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
                            return true;
                    }
                }
                return false;
            }
            else
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
}
