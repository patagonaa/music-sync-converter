using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
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
            var readDirsTask = ReadDirs(config, compareBlock, cancellationToken);

            // wait until last pipeline element is done
            await writeBlock.Completion;

            // delete additional files and empty directories
            DeleteAdditionalFiles(config, handledFiles);
            DeleteEmptySubdirectories(config.TargetDir);
        }

        private async Task ReadDirs(SyncConfig config, ITargetBlock<SourceFileInfo> files, CancellationToken cancellationToken)
        {
            try
            {
                await ReadDir(config, new DirectoryInfo(config.SourceDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar), files, cancellationToken);
                files.Complete();
            }
            catch (Exception ex)
            {
                files.Fault(ex);
            }
        }

        private async Task ReadDir(SyncConfig config, DirectoryInfo dir, ITargetBlock<SourceFileInfo> files, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirLength = config.SourceDir.TrimEnd(Path.DirectorySeparatorChar).Length + 1;

            if (config.Exclude?.Contains(dir.FullName.Substring(sourceDirLength)) ?? false)
            {
                return;
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

                if (config.SourceExtensions.Contains(file.Extension))
                {
                    var path = file.FullName.Substring(sourceDirLength);
                    await files.SendAsync(new SourceFileInfo
                    {
                        RelativePath = path,
                        AbsolutePath = file.FullName,
                        ModifiedDate = file.LastWriteTime
                    });
                }
            }

            foreach (var subDir in dir.EnumerateDirectories())
            {
                await ReadDir(config, subDir, files, cancellationToken);
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
                File.Delete(file.Path); // delete the file if it exists to avoid case sensitivity issues
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

        private void DeleteAdditionalFiles(SyncConfig config, ConcurrentBag<string> handledFiles)
        {
            foreach (var targetFileFull in Directory.GetFiles(config.TargetDir, "*", SearchOption.AllDirectories))
            {
                if (!handledFiles.Contains(targetFileFull))
                {
                    Console.WriteLine($"Delete {targetFileFull}");
                    File.Delete(targetFileFull);
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
