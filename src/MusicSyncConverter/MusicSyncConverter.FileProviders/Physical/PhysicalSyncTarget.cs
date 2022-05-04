using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using MusicSyncConverter.FileProviders.Abstractions;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.Physical
{
    class PhysicalSyncTarget : PhysicalFileProvider, ISyncTarget
    {
        private readonly string _basePath;

        public PhysicalSyncTarget(string basePath)
            : base(basePath, ExclusionFilters.None)
        {
            _basePath = basePath;
        }

        public async Task WriteFile(string path, Stream content, DateTimeOffset modified, CancellationToken cancellationToken)
        {
            var absolutePath = Path.Join(_basePath, path);

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

            File.Delete(absolutePath); // delete the file if it exists to allow for name case changes is on a case-insensitive filesystem
            using (var outputFile = File.OpenWrite(absolutePath))
            {
                await content.CopyToAsync(outputFile, cancellationToken);
            }

            File.SetLastWriteTime(absolutePath, modified.LocalDateTime);
        }

        public bool IsCaseSensitive()
        {
            Directory.CreateDirectory(_basePath);
            var path1 = Path.Combine(_basePath, "test.tmp");
            var path2 = Path.Combine(_basePath, "TEST.tmp");
            File.Delete(path1);
            File.Delete(path2);
            using (File.Create(path1))
            {
            }

            var toReturn = !File.Exists(path2);

            File.Delete(path1);
            return toReturn;
        }

        public void Delete(IFileInfo file)
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
        }
    }
}
