using Microsoft.Extensions.FileProviders;
using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    class SyncService
    {
        private readonly FileProviderFactory _fileProviderFactory;
        private readonly TextSanitizer _sanitizer;
        private readonly MediaAnalyzer _analyzer;
        private readonly MediaConverter _converter;
        private readonly FatSorter _fatSorter;
        private readonly PathMatcher _pathMatcher;

        private static readonly TimeSpan _fileTimestampDelta = TimeSpan.FromSeconds(2); // FAT32 write time has 2 seconds precision

        public SyncService()
        {
            _fileProviderFactory = new FileProviderFactory();
            _sanitizer = new TextSanitizer();
            _analyzer = new MediaAnalyzer(_sanitizer);
            _converter = new MediaConverter();
            _fatSorter = new FatSorter();
            _pathMatcher = new PathMatcher();
        }

        public async Task Run(SyncConfig config, CancellationToken upstreamCancellationToken)
        {
            using var cancallationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(upstreamCancellationToken);
            var cancellationToken = cancallationTokenSource.Token;
            CleanupTempDir(cancellationToken);

            //set up pipeline
            var readOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = config.WorkersRead,
                MaxDegreeOfParallelism = config.WorkersRead,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var workerOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = config.WorkersConvert,
                MaxDegreeOfParallelism = config.WorkersConvert,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var writeOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = config.WorkersWrite,
                MaxDegreeOfParallelism = config.WorkersWrite,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var targetCaseSensitive = IsCaseSensitive(config.TargetDir);

            var handledFiles = new ConcurrentBag<string>();
            var updatedDirs = new ConcurrentBag<string>();
            var infoLogMessages = new ConcurrentBag<string>();

            var compareBlock = new TransformManyBlock<SourceFileInfo, ReadWorkItem>(x => new[] { CompareDates(config, x) }.Where(y => y != null), workerOptions);
            var readBlock = new TransformBlock<ReadWorkItem, AnalyzeWorkItem>(async x => await Read(x, cancellationToken), readOptions);
            var analyzeBlock = new TransformManyBlock<AnalyzeWorkItem, ConvertWorkItem>(async x => new[] { await _analyzer.Analyze(config, x, infoLogMessages) }.Where(y => y != null), workerOptions);
            var convertBlock = new TransformManyBlock<ConvertWorkItem, OutputFile>(async x => new[] { await Convert(x, handledFiles, cancellationToken) }.Where(y => y != null), workerOptions);
            var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, updatedDirs, cancellationToken), writeOptions);

            compareBlock.LinkTo(readBlock, new DataflowLinkOptions { PropagateCompletion = true });
            readBlock.LinkTo(analyzeBlock, new DataflowLinkOptions { PropagateCompletion = true });
            analyzeBlock.LinkTo(convertBlock, new DataflowLinkOptions { PropagateCompletion = true });
            convertBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = true });

            var fileProvider = _fileProviderFactory.Get(config.SourceDir);
            using (fileProvider as IDisposable)
            {
                // start pipeline by adding files to check for changes
                _ = ReadDirs(config, fileProvider, compareBlock, targetCaseSensitive, cancellationToken);

                // wait until last pipeline element is done
                try
                {
                    await writeBlock.Completion;
                }
                catch (Exception)
                {
                    // cancel everything as faults only get propagated to the blocks after the fault
                    // and we need all blocks to stop so we can exit the application
                    cancallationTokenSource.Cancel();
                    throw;
                }
            }

            var pathComparer = new PathComparer(targetCaseSensitive);

            // delete additional files and empty directories
            DeleteAdditionalFiles(config, handledFiles, pathComparer, cancellationToken);
            DeleteEmptySubdirectories(config.TargetDir, cancellationToken);

            foreach (var updatedDir in updatedDirs.Distinct(pathComparer).OrderByDescending(x => x.Length))
            {
                _fatSorter.Sort(updatedDir, config.DeviceConfig.FatSortMode, false, cancellationToken);
            }

            foreach (var infoLogMessage in infoLogMessages.Distinct())
            {
                Console.WriteLine(infoLogMessage);
            }
        }

        private async Task ReadDirs(SyncConfig config, IFileProvider fileProvider, ITargetBlock<SourceFileInfo> targetBlock, bool targetCaseSensitive, CancellationToken cancellationToken)
        {
            try
            {
                var r = new Random();
                var files = ReadDir(config, fileProvider, "", targetCaseSensitive, cancellationToken)
                    .OrderBy(x => r.Next()); // randomize order so IO- and CPU-heavy actions are more balanced
                foreach (var file in files)
                {
                    await targetBlock.SendAsync(file, cancellationToken);
                }
                targetBlock.Complete();
            }
            catch (Exception ex)
            {
                targetBlock.Fault(ex);
                throw;
            }
        }

        private void CleanupTempDir(CancellationToken cancellationToken)
        {
            var thresholdDate = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var file in Directory.GetFiles(TempFileHelper.GetTempPath()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTimeUtc < thresholdDate)
                    fileInfo.Delete();
            }
        }

        private bool IsCaseSensitive(string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            var path1 = Path.Combine(targetDir, "test.tmp");
            var path2 = Path.Combine(targetDir, "TEST.tmp");
            File.Delete(path1);
            File.Delete(path2);
            using (File.Create(path1))
            {
            }

            var toReturn = !File.Exists(path2);

            File.Delete(path1);
            return toReturn;
        }

        private IEnumerable<SourceFileInfo> ReadDir(SyncConfig config, IFileProvider fileProvider, string dir, bool caseSensitive, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (config.Exclude?.Any(glob => _pathMatcher.Matches(glob, dir, caseSensitive)) ?? false)
            {
                yield break;
            }
            var directoryContents = fileProvider.GetDirectoryContents(dir).ToList();
            foreach (var file in directoryContents.Where(x => !x.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (config.SourceExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase))
                {
                    yield return new SourceFileInfo
                    {
                        FileProvider = fileProvider,
                        RelativePath = Path.Join(dir, file.Name),
                        ModifiedDate = file.LastModified.LocalDateTime
                    };
                }
            }

            foreach (var subDir in directoryContents.Where(x => x.IsDirectory))
            {
                foreach (var file in ReadDir(config, fileProvider, Path.Join(dir, subDir.Name), caseSensitive, cancellationToken))
                {
                    yield return file;
                }
            }
        }

        private ReadWorkItem CompareDates(SyncConfig config, SourceFileInfo sourceFile)
        {
            try
            {
                var targetFilePath = Path.Combine(config.TargetDir, _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, sourceFile.RelativePath, true, out _));
                string targetDirPath = Path.GetDirectoryName(targetFilePath);

                var directoryInfo = new DirectoryInfo(targetDirPath);
                var targetInfos = directoryInfo.Exists ? directoryInfo.GetFiles($"{Path.GetFileNameWithoutExtension(targetFilePath)}.*") : Array.Empty<FileInfo>();

                if (targetInfos.Length != 1) // zero or multiple target files
                {
                    // if there are multiple target files, delete them
                    foreach (var targetInfo in targetInfos)
                    {
                        Console.WriteLine($"Deleting ambiguous file {targetInfo.FullName}");
                        targetInfo.Delete();
                    }
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Replace,
                        SourceFileInfo = sourceFile
                    };
                }

                // only one target item, so check if it is up to date
                var targetDate = targetInfos[0].LastWriteTime;
                if (FileDatesEqual(sourceFile.ModifiedDate, targetDate))
                {
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Keep,
                        SourceFileInfo = sourceFile,
                        ExistingTargetFile = targetInfos[0].FullName
                    };
                }
                else
                {
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Replace,
                        SourceFileInfo = sourceFile
                    };
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private bool FileDatesEqual(DateTime dateTime, DateTime dateTime1)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // this is really stupid but to handle this properly would be a lot of effort
                // https://twitter.com/patagona/status/1516123536275910658
                return (dateTime - dateTime1).Duration() <= _fileTimestampDelta ||
                    (dateTime - dateTime1 - TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta ||
                    (dateTime - dateTime1 + TimeSpan.FromHours(1)).Duration() <= _fileTimestampDelta;
            }

            return (dateTime - dateTime1).Duration() <= _fileTimestampDelta;
        }

        private async Task<AnalyzeWorkItem> Read(ReadWorkItem workItem, CancellationToken cancellationToken)
        {
            AnalyzeWorkItem toReturn;
            switch (workItem.ActionType)
            {
                case CompareResultType.Keep:
                    toReturn = new AnalyzeWorkItem
                    {
                        ActionType = AnalyzeActionType.Keep,
                        SourceFileInfo = workItem.SourceFileInfo,
                        ExistingTargetFile = workItem.ExistingTargetFile
                    };
                    break;
                case CompareResultType.Replace:
                    Console.WriteLine($"--> Read {workItem.SourceFileInfo.RelativePath}");

                    string tmpFilePath;
                    using (var inFile = workItem.SourceFileInfo.FileProvider.GetFileInfo(workItem.SourceFileInfo.RelativePath).CreateReadStream())
                    {
                        tmpFilePath = await TempFileHelper.CopyToTempFile(inFile, cancellationToken);
                    }

                    toReturn = new AnalyzeWorkItem
                    {
                        ActionType = AnalyzeActionType.CopyOrConvert,
                        SourceFileInfo = workItem.SourceFileInfo,
                        SourceTempFilePath = tmpFilePath
                    };
                    Console.WriteLine($"<-- Read {workItem.SourceFileInfo.RelativePath}");
                    break;
                default:
                    throw new ArgumentException("Invalid ReadActionType");
            }
            return toReturn;
        }

        public async Task<OutputFile> Convert(ConvertWorkItem workItem, ConcurrentBag<string> handledFiles, CancellationToken cancellationToken)
        {
            try
            {
                OutputFile toReturn = null;
                switch (workItem.ActionType)
                {
                    case ConvertActionType.Keep:
                        handledFiles.Add(workItem.TargetFilePath);
                        return null;
                    case ConvertActionType.Transcode:
                    case ConvertActionType.Remux:
                        {
                            var sw = Stopwatch.StartNew();
                            Console.WriteLine($"--> {workItem.ActionType} {workItem.SourceFileInfo.RelativePath}");
                            var sourcePath = workItem.SourceTempFilePath;
                            var outputFormat = workItem.EncoderInfo;

                            var outPath = await _converter.Convert(sourcePath, outputFormat, workItem.Tags, cancellationToken);

                            handledFiles.Add(workItem.TargetFilePath);
                            toReturn = new OutputFile
                            {
                                Path = workItem.TargetFilePath,
                                ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                TempFilePath = outPath
                            };
                            sw.Stop();
                            Console.WriteLine($"<-- {workItem.ActionType} {sw.ElapsedMilliseconds}ms {workItem.SourceFileInfo.RelativePath}");
                            break;
                        }
                    default:
                        break;
                }
                return toReturn;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine(ex);
                    throw;
                }
            }
            finally
            {
                if (workItem.SourceTempFilePath != null)
                {
                    File.Delete(workItem.SourceTempFilePath);
                }
            }
            return null;
        }

        private async Task WriteFile(OutputFile file, ConcurrentBag<string> updatedDirectories, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"--> Write {file.Path}");
                var directory = Path.GetDirectoryName(file.Path);

                var dirInfo = new DirectoryInfo(directory);
                updatedDirectories.Add(dirInfo.FullName);
                while (!dirInfo.Exists)
                {
                    dirInfo = dirInfo.Parent;
                    updatedDirectories.Add(dirInfo.FullName);
                }

                Directory.CreateDirectory(directory);

                File.Delete(file.Path); // delete the file if it exists to allow for name case changes is on a case-insensitive filesystem
                using (var tmpFile = File.OpenRead(file.TempFilePath))
                {
                    using (var outputFile = File.OpenWrite(file.Path))
                    {
                        await tmpFile.CopyToAsync(outputFile, cancellationToken);
                    }
                }
                File.Delete(file.TempFilePath);
                File.SetLastWriteTime(file.Path, file.ModifiedDate);
                Console.WriteLine($"<-- Write {file.Path}");
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        private void DeleteAdditionalFiles(SyncConfig config, ConcurrentBag<string> handledFiles, IEqualityComparer<string> pathComparer, CancellationToken cancellationToken)
        {
            foreach (var targetFileFull in Directory.GetFiles(config.TargetDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!handledFiles.Contains(targetFileFull, pathComparer))
                {
                    Console.WriteLine($"Delete {targetFileFull}");
                    File.Delete(targetFileFull);

                    // if this happens, we most likely ran into a stupid Windows VFAT Unicode bug that points different filenames to the same or separate files depending on the operation.
                    // https://twitter.com/patagona/status/1444626808935264261
                    if (File.Exists(targetFileFull))
                    {
                        File.Delete(targetFileFull);
                        Console.WriteLine($"Couldn't delete {targetFileFull} on the first try. This is probably due to a Windows bug related to VFAT (FAT32) handling. Re-Run sync to fix this.");
                    }
                }
            }
        }

        private void DeleteEmptySubdirectories(string parentDirectory, CancellationToken cancellationToken)
        {
            foreach (string directory in Directory.GetDirectories(parentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteEmptySubdirectories(directory, cancellationToken);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Console.WriteLine($"Delete {directory}");
                    Directory.Delete(directory, false);
                }
            }
        }
    }
}
