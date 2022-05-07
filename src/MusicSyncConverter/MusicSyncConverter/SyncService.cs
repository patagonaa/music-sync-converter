﻿using Microsoft.Extensions.FileProviders;
using MusicSyncConverter.Config;
using MusicSyncConverter.FileProviders;
using MusicSyncConverter.FileProviders.Abstractions;
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
        private readonly SyncTargetFactory _syncTargetFactory;
        private readonly TextSanitizer _sanitizer;
        private readonly MediaConverter _converter;
        private readonly PathMatcher _pathMatcher;

        private static readonly TimeSpan _fileTimestampDelta = TimeSpan.FromSeconds(2); // FAT32 write time has 2 seconds precision

        public SyncService()
        {
            _fileProviderFactory = new FileProviderFactory();
            _syncTargetFactory = new SyncTargetFactory();
            _sanitizer = new TextSanitizer();
            _converter = new MediaConverter();
            _pathMatcher = new PathMatcher();
        }

        public async Task Run(SyncConfig config, CancellationToken upstreamCancellationToken)
        {
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(upstreamCancellationToken);
            var cancellationToken = cancellationTokenSource.Token;
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

            var handledFiles = new ConcurrentBag<string>();
            var updatedDirs = new ConcurrentBag<string>();
            var infoLogMessages = new ConcurrentBag<string>();

            var syncTarget = _syncTargetFactory.Get(config.TargetDir);

            using (syncTarget as IDisposable)
            {
                var targetCaseSensitive = syncTarget.IsCaseSensitive();
                var pathComparer = new PathComparer(targetCaseSensitive);

                var compareBlock = new TransformManyBlock<SourceFileInfo, ReadWorkItem>(x => new[] { CompareDates(config, pathComparer, syncTarget, x) }.Where(y => y != null), readOptions);
                var readBlock = new TransformBlock<ReadWorkItem, ConvertWorkItem>(async x => await Read(x, config, infoLogMessages, cancellationToken), readOptions);
                var convertBlock = new TransformManyBlock<ConvertWorkItem, OutputFile>(async x => new[] { await Convert(x, config, infoLogMessages, handledFiles, cancellationToken) }.Where(y => y != null), workerOptions);
                var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, syncTarget, cancellationToken), writeOptions);

                compareBlock.LinkTo(readBlock, new DataflowLinkOptions { PropagateCompletion = true });
                readBlock.LinkTo(convertBlock, new DataflowLinkOptions { PropagateCompletion = true });
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
                        cancellationTokenSource.Cancel();
                        throw;
                    }
                }

                // delete additional files and empty directories
                DeleteAdditionalFiles(config, syncTarget, handledFiles, pathComparer, cancellationToken);
                DeleteEmptySubdirectories("", syncTarget, cancellationToken);

                await syncTarget.Complete(cancellationToken);
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
                var files = ReadDir(config, fileProvider, "", targetCaseSensitive, cancellationToken);
                //.OrderBy(x => r.Next()); // randomize order so IO- and CPU-heavy actions are more balanced
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
                        ModifiedDate = file.LastModified
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

        private ReadWorkItem CompareDates(SyncConfig config, PathComparer pathComparer, ISyncTarget syncTarget, SourceFileInfo sourceFile)
        {
            try
            {
                var relativeTargetPath = _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, sourceFile.RelativePath, true, out _);
                string targetDirPath = Path.GetDirectoryName(relativeTargetPath);

                var files = syncTarget.GetDirectoryContents(targetDirPath);

                var targetInfos = files.Exists ? files.Where(x => pathComparer.Equals(Path.GetFileNameWithoutExtension(relativeTargetPath), Path.GetFileNameWithoutExtension(x.Name))).ToArray() : Array.Empty<IFileInfo>();

                if (targetInfos.Length != 1) // zero or multiple target files
                {
                    //TODO
                    //// if there are multiple target files, delete them
                    //foreach (var targetInfo in targetInfos)
                    //{
                    //    Console.WriteLine($"Deleting ambiguous file {targetInfo.FullName}");
                    //    targetInfo.Delete();
                    //}
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Replace,
                        SourceFileInfo = sourceFile
                    };
                }

                // only one target item, so check if it is up to date
                var targetDate = targetInfos[0].LastModified;
                if (FileDatesEqual(sourceFile.ModifiedDate, targetDate))
                {
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Keep,
                        SourceFileInfo = sourceFile,
                        ExistingTargetFile = Path.Join(targetDirPath, targetInfos[0].Name)
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

        private bool FileDatesEqual(DateTimeOffset dateTime, DateTimeOffset dateTime1)
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

        private async Task<ConvertWorkItem> Read(ReadWorkItem workItem, SyncConfig config, IProducerConsumerCollection<string> infoLogMessages, CancellationToken cancellationToken)
        {
            ConvertWorkItem toReturn;
            switch (workItem.ActionType)
            {
                case CompareResultType.Keep:
                    toReturn = new ConvertWorkItem
                    {
                        ActionType = ConvertActionType.Keep,
                        SourceFileInfo = workItem.SourceFileInfo,
                        TargetFilePath = workItem.ExistingTargetFile
                    };
                    break;
                case CompareResultType.Replace:
                    Console.WriteLine($"--> Read {workItem.SourceFileInfo.RelativePath}");

                    var fileProvider = workItem.SourceFileInfo.FileProvider;

                    string tmpFilePath;
                    using (var inFile = fileProvider.GetFileInfo(workItem.SourceFileInfo.RelativePath).CreateReadStream())
                    {
                        tmpFilePath = await TempFileHelper.CopyToTempFile(inFile, cancellationToken);
                    }

                    var coverVariants = new[] { "cover.png", "cover.jpg", "folder.jpg" };
                    string albumCoverPath = null;
                    foreach (var coverVariant in coverVariants)
                    {
                        var coverFileInfo = fileProvider.GetFileInfo(Path.Join(Path.GetDirectoryName(workItem.SourceFileInfo.RelativePath), coverVariant));
                        if (coverFileInfo.Exists)
                        {
                            if (coverFileInfo.PhysicalPath != null)
                            {
                                albumCoverPath = coverFileInfo.PhysicalPath;
                            }
                            else
                            {
                                using (var coverStream = coverFileInfo.CreateReadStream())
                                {
                                    albumCoverPath = await TempFileHelper.CopyToTempFile(coverStream, cancellationToken);
                                }
                            }
                            break;
                        }
                    }

                    var targetFilePath = _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, workItem.SourceFileInfo.RelativePath, true, out var hasUnsupportedChars);
                    if (hasUnsupportedChars)
                        infoLogMessages.TryAdd($"Unsupported chars in path: {workItem.SourceFileInfo.RelativePath}");

                    toReturn = new ConvertWorkItem
                    {
                        ActionType = ConvertActionType.RemuxOrConvert,
                        SourceFileInfo = workItem.SourceFileInfo,
                        SourceTempFilePath = tmpFilePath,
                        TargetFilePath = targetFilePath,
                        AlbumArtPath = albumCoverPath
                    };
                    Console.WriteLine($"<-- Read {workItem.SourceFileInfo.RelativePath}");
                    break;
                default:
                    throw new ArgumentException("Invalid ReadActionType");
            }
            return toReturn;
        }

        public async Task<OutputFile> Convert(ConvertWorkItem workItem, SyncConfig config, IProducerConsumerCollection<string> infoLogMessages, ConcurrentBag<string> handledFiles, CancellationToken cancellationToken)
        {
            try
            {
                switch (workItem.ActionType)
                {
                    case ConvertActionType.Keep:
                        handledFiles.Add(workItem.TargetFilePath);
                        return null;
                    case ConvertActionType.RemuxOrConvert:
                        {
                            var sw = Stopwatch.StartNew();
                            Console.WriteLine($"--> Convert {workItem.SourceFileInfo.RelativePath}");
                            var outFile = await _converter.RemuxOrConvert(config, workItem, infoLogMessages, cancellationToken);
                            handledFiles.Add(outFile.Path);
                            sw.Stop();
                            Console.WriteLine($"<-- Convert {sw.ElapsedMilliseconds}ms {workItem.SourceFileInfo.RelativePath}");
                            return outFile;
                        }
                    default:
                        throw new ArgumentException("Invalid ConvertActionType");
                }
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

        private async Task WriteFile(OutputFile file, ISyncTarget syncTarget, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"--> Write {file.Path}");

                using (var tmpFile = File.OpenRead(file.TempFilePath))
                {
                    await syncTarget.WriteFile(file.Path, tmpFile, file.ModifiedDate, cancellationToken);
                }

                File.Delete(file.TempFilePath);
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

        private void DeleteAdditionalFiles(SyncConfig config, ISyncTarget syncTarget, ConcurrentBag<string> handledFiles, IEqualityComparer<string> pathComparer, CancellationToken cancellationToken)
        {
            var files = GetAllFiles("", syncTarget);
            var toDelete = new List<IFileInfo>();
            foreach (var (path, file) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!handledFiles.Contains(path, pathComparer) && !file.Name.StartsWith('.'))
                {
                    toDelete.Add(file);
                    Console.WriteLine($"Delete {path}");
                }
            }
            syncTarget.Delete(toDelete, cancellationToken);
        }

        private IEnumerable<(string Path, IFileInfo File)> GetAllFiles(string directoryPath, ISyncTarget syncTarget)
        {
            foreach (var fileInfo in syncTarget.GetDirectoryContents(directoryPath))
            {
                var filePath = Path.Join(directoryPath, fileInfo.Name);
                if (fileInfo.IsDirectory)
                {
                    foreach (var file in GetAllFiles(filePath, syncTarget))
                    {
                        yield return file;
                    }
                }
                else
                {
                    yield return (filePath, fileInfo);
                }
            }
        }

        private void DeleteEmptySubdirectories(string path, ISyncTarget syncTarget, CancellationToken cancellationToken)
        {
            foreach (var item in syncTarget.GetDirectoryContents(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.IsDirectory)
                    continue;

                string subDir = Path.Join(path, item.Name);
                DeleteEmptySubdirectories(subDir, syncTarget, cancellationToken);
                if (!syncTarget.GetDirectoryContents(subDir).Any())
                {
                    Console.WriteLine($"Delete {subDir}");
                    syncTarget.Delete(item, cancellationToken);
                }
            }
        }
    }
}
