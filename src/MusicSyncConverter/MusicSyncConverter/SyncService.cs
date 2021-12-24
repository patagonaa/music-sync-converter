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
        private readonly TextSanitizer _sanitizer;
        private readonly MediaAnalyzer _analyzer;
        private readonly MediaConverter _converter;

        public SyncService()
        {
            _sanitizer = new TextSanitizer();
            _analyzer = new MediaAnalyzer(_sanitizer);
            _converter = new MediaConverter();
        }

        public async Task Run(SyncConfig config, CancellationToken cancellationToken)
        {
            //set up pipeline
            var readOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = Math.Max(16, config.WorkersRead),
                MaxDegreeOfParallelism = config.WorkersRead,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var workerOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = Math.Max(8, config.WorkersConvert),
                MaxDegreeOfParallelism = config.WorkersConvert,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var writeOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = Math.Max(8, config.WorkersWrite),
                MaxDegreeOfParallelism = config.WorkersWrite,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var targetCaseSensitive = IsCaseSensitive(config.TargetDir);

            var handledFiles = new ConcurrentBag<string>();

            var compareBlock = new TransformManyBlock<SourceFileInfo, ReadWorkItem>(x => new ReadWorkItem[] { CompareDates(config, x) }.Where(y => y != null), workerOptions);
            var readBlock = new TransformBlock<ReadWorkItem, AnalyzeWorkItem>(async x => await Read(x, cancellationToken), readOptions);
            var analyzeBlock = new TransformManyBlock<AnalyzeWorkItem, ConvertWorkItem>(async x => new ConvertWorkItem[] { await _analyzer.Analyze(config, x) }.Where(y => y != null), workerOptions);
            var convertBlock = new TransformManyBlock<ConvertWorkItem, OutputFile>(async x => new OutputFile[] { await Convert(x, handledFiles, cancellationToken) }.Where(y => y != null), workerOptions);
            var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, cancellationToken), writeOptions);

            compareBlock.LinkTo(readBlock, new DataflowLinkOptions { PropagateCompletion = true });
            readBlock.LinkTo(analyzeBlock, new DataflowLinkOptions { PropagateCompletion = true });
            analyzeBlock.LinkTo(convertBlock, new DataflowLinkOptions { PropagateCompletion = true });
            convertBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = true });

            // start pipeline by adding directories to check for changes
            try
            {
                var r = new Random();
                foreach (var dir in ReadDir(config, new DirectoryInfo(config.SourceDir), cancellationToken).OrderBy(x => r.Next()))
                {
                    await compareBlock.SendAsync(dir);
                }
                compareBlock.Complete();
            }
            catch (Exception ex)
            {
                ((ITargetBlock<SourceFileInfo>)compareBlock).Fault(ex);
            }

            // wait until last pipeline element is done
            await writeBlock.Completion;

            // delete additional files and empty directories
            DeleteAdditionalFiles(config, handledFiles, targetCaseSensitive, cancellationToken);
            DeleteEmptySubdirectories(config.TargetDir);
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

        private IEnumerable<SourceFileInfo> ReadDir(SyncConfig config, DirectoryInfo dir, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (config.Exclude?.Contains(Path.GetRelativePath(config.SourceDir, dir.FullName)) ?? false)
            {
                yield break;
            }
            foreach (var file in dir.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Skip files not locally on disk (if source is OneDrive / NextCloud and virtual files are used)
                if (file.Attributes.HasFlag((FileAttributes)0x400000) || // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
                    file.Attributes.HasFlag((FileAttributes)0x40000) // FILE_ATTRIBUTE_RECALL_ON_OPEN
                    )
                {
                    continue;
                }

                if (config.SourceExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    var path = Path.GetRelativePath(config.SourceDir, file.FullName);
                    yield return new SourceFileInfo
                    {
                        RelativePath = path,
                        AbsolutePath = file.FullName,
                        ModifiedDate = file.LastWriteTime
                    };
                }
            }

            foreach (var subDir in dir.EnumerateDirectories())
            {
                foreach (var file in ReadDir(config, subDir, cancellationToken))
                {
                    yield return file;
                }
            }
        }

        private ReadWorkItem CompareDates(SyncConfig config, SourceFileInfo sourceFile)
        {
            try
            {
                var targetFilePath = Path.Combine(config.TargetDir, _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, sourceFile.RelativePath, true));
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
                if (Math.Abs((sourceFile.ModifiedDate - targetDate).TotalMinutes) > 1)
                {
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Replace,
                        SourceFileInfo = sourceFile
                    };
                }
                else
                {
                    return new ReadWorkItem
                    {
                        ActionType = CompareResultType.Keep,
                        SourceFileInfo = sourceFile,
                        ExistingTargetFile = targetInfos[0].FullName
                    };
                }
            }
            catch (Exception)
            {
                throw;
            }
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
                    Console.WriteLine($"--> Read {workItem.SourceFileInfo.AbsolutePath}");

                    // i'm not sure if this is smart or dumb but it definitely improves performance in cases where the source drive is slow and the system drive is fast
                    string tmpFilePath = TempFileHelper.GetTempFilePath();
                    using (var tmpFile = File.Create(tmpFilePath))
                    {
                        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                        {
                            // this should in theory cause Windows to try to keep the file contents in RAM instead of writing to disk
                            // on linux/unix this should be tmpfs anyway
                            File.SetAttributes(tmpFilePath, FileAttributes.Temporary);
                        }

                        using (var inFile = File.OpenRead(workItem.SourceFileInfo.AbsolutePath))
                        {
                            await inFile.CopyToAsync(tmpFile, cancellationToken);
                        }
                    }

                    toReturn = new AnalyzeWorkItem
                    {
                        ActionType = AnalyzeActionType.CopyOrConvert,
                        SourceFileInfo = workItem.SourceFileInfo,
                        SourceTempFilePath = tmpFilePath
                    };
                    Console.WriteLine($"<-- Read {workItem.SourceFileInfo.AbsolutePath}");
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
                    case ConvertActionType.Copy:
                        {
                            Console.WriteLine($"--> Read {workItem.SourceFileInfo.RelativePath}");
                            handledFiles.Add(workItem.TargetFilePath);
                            toReturn = new OutputFile
                            {
                                Path = workItem.TargetFilePath,
                                ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                Content = await File.ReadAllBytesAsync(workItem.SourceTempFilePath, cancellationToken)
                            };
                            Console.WriteLine($"<-- Read {workItem.SourceFileInfo.RelativePath}");
                            break;
                        }
                    case ConvertActionType.Transcode:
                    case ConvertActionType.Remux:
                        {
                            var sw = Stopwatch.StartNew();
                            Console.WriteLine($"--> {workItem.ActionType} {workItem.SourceFileInfo.RelativePath}");
                            var sourcePath = workItem.SourceTempFilePath ?? workItem.SourceFileInfo.AbsolutePath;
                            var outputFormat = workItem.EncoderInfo;

                            var outBytes = await _converter.Convert(sourcePath, outputFormat, workItem.Tags, cancellationToken);

                            handledFiles.Add(workItem.TargetFilePath);
                            toReturn = new OutputFile
                            {
                                Path = workItem.TargetFilePath,
                                ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                Content = outBytes
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

        private async Task WriteFile(OutputFile file, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"--> Write {file.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path));
                File.Delete(file.Path); // delete the file if it exists to allow for name case changes is on a case-insensitive filesystem
                await File.WriteAllBytesAsync(file.Path, file.Content, cancellationToken);
                File.SetLastWriteTime(file.Path, file.ModifiedDate);
                Console.WriteLine($"<-- Write {file.Path}");
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        private void DeleteAdditionalFiles(SyncConfig config, ConcurrentBag<string> handledFiles, bool targetCaseSensitive, CancellationToken cancellationToken)
        {
            foreach (var targetFileFull in Directory.GetFiles(config.TargetDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!handledFiles.Contains(targetFileFull, targetCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
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

        private void DeleteEmptySubdirectories(string parentDirectory)
        {
            foreach (string directory in Directory.GetDirectories(parentDirectory))
            {
                DeleteEmptySubdirectories(directory);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Console.WriteLine($"Delete {directory}");
                    Directory.Delete(directory, false);
                }
            }
        }
    }
}
