using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using MusicSyncConverter.FileProviders.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.Physical
{
    class PhysicalSyncTarget : PhysicalFileProvider, ISyncTarget
    {
        private readonly string _basePath;
        private readonly FatSortMode _sortMode;
        private readonly bool _isCaseSensitive;
        private readonly HashSet<string> _updatedDirectories;
        private readonly FatSorter _fatSorter;

        public PhysicalSyncTarget(string basePath, FatSortMode sortMode)
            : base(basePath, ExclusionFilters.None)
        {
            _basePath = basePath;
            _sortMode = sortMode;
            _isCaseSensitive = IsCaseSensitiveInternal(basePath);
            _updatedDirectories = new HashSet<string>(new PathComparer(_isCaseSensitive));
            _fatSorter = new FatSorter();
        }

        public async Task WriteFile(string path, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            var absolutePath = Path.Join(_basePath, path);

            var dirInfo = new DirectoryInfo(Path.GetDirectoryName(absolutePath));
            _updatedDirectories.Add(dirInfo.FullName);
            while (!dirInfo.Exists)
            {
                dirInfo = dirInfo.Parent;
                _updatedDirectories.Add(dirInfo.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

            File.Delete(absolutePath); // delete the file if it exists to allow for name case changes is on a case-insensitive filesystem
            using (var outputFile = File.OpenWrite(absolutePath))
            {
                await content.CopyToAsync(outputFile, cancellationToken);
            }

            if (modified.HasValue)
                File.SetLastWriteTime(absolutePath, modified.Value.LocalDateTime);
        }

        public bool IsCaseSensitive() => _isCaseSensitive;

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

        public Task Delete(IFileInfo file, CancellationToken cancellationToken)
        {
            if (file is PhysicalFileInfo)
            {
                var targetFileFull = file.PhysicalPath;

                File.Delete(targetFileFull);

                // if this happens, we most likely ran into a stupid Windows VFAT Unicode bug that points different filenames to the same or separate files depending on the operation.
                // https://twitter.com/patagona/status/1444626808935264261
                if (File.Exists(targetFileFull))
                {
                    File.Delete(targetFileFull);
                    Console.WriteLine($"Couldn't delete {targetFileFull} on the first try. This is probably due to a Windows bug related to VFAT (FAT32) handling. Re-Run sync to fix this.");
                }
            }
            else if (file is PhysicalDirectoryInfo)
            {
                Directory.Delete(file.PhysicalPath);
            }
            else
            {
                throw new ArgumentException("file must be PhysicalFileInfo or PhysicalDirectoryInfo", nameof(file));
            }
            return Task.CompletedTask;
        }

        public Task Delete(IReadOnlyCollection<IFileInfo> files, CancellationToken cancellationToken)
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
            foreach (var updatedDir in _updatedDirectories.OrderByDescending(x => x.Length))
            {
                _fatSorter.Sort(updatedDir, _sortMode, false, cancellationToken);
            }
            return Task.CompletedTask;
        }
    }
}
