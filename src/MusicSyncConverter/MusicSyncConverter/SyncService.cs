using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using MusicSyncConverter.Config;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    class SyncService
    {
        public static readonly IReadOnlyCollection<string> _supportedExtensions = new HashSet<string>
        {
            ".mp3",
            ".ogg",
            ".m4a",
            ".flac",
            ".opus",
            ".wma"
        };

        public async Task Run(SyncConfig config)
        {
            //set up pipeline
            var workerOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 16,
                MaxDegreeOfParallelism = config.WorkersConvert,
                EnsureOrdered = false
            };

            var writeOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 8,
                MaxDegreeOfParallelism = config.WorkersWrite,
                EnsureOrdered = false
            };

            var compareBlock = new TransformManyBlock<SourceFile, WorkItem>(async x => new WorkItem[] { await Compare(config, x) }.Where(y => y != null), workerOptions);

            var handledFiles = new ConcurrentBag<string>();
            var handleBlock = new TransformManyBlock<WorkItem, OutputFile>(async x => new OutputFile[] { await HandleWorkItem(config, x, handledFiles) }.Where(y => y != null), workerOptions);

            compareBlock.LinkTo(handleBlock, new DataflowLinkOptions { PropagateCompletion = true });

            var writeBlock = new ActionBlock<OutputFile>(WriteFile, writeOptions);

            handleBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = true });

            // start pipeline by adding directories to check for changes
            await ReadDirs(config, compareBlock);

            // wait until last pipeline element is done
            await writeBlock.Completion;

            // delete additional files and empty directories
            DeleteAdditionalFiles(config, handledFiles);
            DeleteEmptySubdirectories(config.TargetDir);
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

        private async Task ReadDirs(SyncConfig config, ITargetBlock<SourceFile> files)
        {
            await ReadDir(config, new DirectoryInfo(config.SourceDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar), files);
            files.Complete();
        }

        private async Task ReadDir(SyncConfig config, DirectoryInfo dir, ITargetBlock<SourceFile> files)
        {
            var sourceDirLength = config.SourceDir.TrimEnd(Path.DirectorySeparatorChar).Length + 1;

            if (config.Exclude.Contains(dir.FullName.Substring(sourceDirLength)))
            {
                return;
            }
            foreach (var file in dir.EnumerateFiles())
            {
                // Skip files not locally on disk (if source is OneDrive / NextCloud and virtual files are used)
                if (file.Attributes.HasFlag((FileAttributes)0x400000) || // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
                    file.Attributes.HasFlag((FileAttributes)0x40000) // FILE_ATTRIBUTE_RECALL_ON_OPEN
                    )
                {
                    continue;
                }

                if (_supportedExtensions.Contains(file.Extension))
                {
                    var path = file.FullName.Substring(sourceDirLength);
                    await files.SendAsync(new SourceFile
                    {
                        Path = path,
                        ModifiedDate = file.LastWriteTime
                    });
                }
            }

            foreach (var subDir in dir.EnumerateDirectories())
            {
                await ReadDir(config, subDir, files);
            }
        }

        private async Task<WorkItem> Compare(SyncConfig config, SourceFile sourceFile)
        {
            try
            {
                var targetFilePath = Path.Combine(config.TargetDir, config.DeviceConfig.CharacterLimitations != null ? SanitizePath(config.DeviceConfig.CharacterLimitations, sourceFile.Path) : sourceFile.Path);
                string targetDirPath = Path.GetDirectoryName(targetFilePath);

                var directoryInfo = new DirectoryInfo(targetDirPath);
                var targetInfo = directoryInfo.Exists ? directoryInfo.GetFiles($"{Path.GetFileNameWithoutExtension(targetFilePath)}.*") : new FileInfo[0];

                if (targetInfo.Length == 0)
                {
                    return await GetWorkItemCopyOrConvert(config, sourceFile, targetFilePath);
                }
                else if (targetInfo.Length == 1)
                {
                    var targetDate = targetInfo[0].LastWriteTime;
                    if (Math.Abs((sourceFile.ModifiedDate - targetDate).TotalMinutes) > 1)
                    {
                        return await GetWorkItemCopyOrConvert(config, sourceFile, targetFilePath);
                    }
                    else
                    {
                        return new WorkItem
                        {
                            ActionType = ActionType.Keep,
                            SourceFile = sourceFile,
                            TargetFilePath = targetInfo[0].FullName
                        };
                    }
                }
                else
                {
                    Console.WriteLine($"Multiple potential existing target files for {sourceFile.Path}");
                }
                return null;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private string SanitizePath(CharacterLimitations config, string path)
        {
            var toReturn = new StringBuilder();

            foreach (var chr in path)
            {
                if (chr == Path.DirectorySeparatorChar || chr == '.')
                {
                    toReturn.Append(chr);
                    continue;
                }

                var replacement = config.Replacements.FirstOrDefault(x => x.Char == chr);
                if (replacement != null)
                {
                    toReturn.Append(replacement.Replacement);
                    continue;
                }

                if (config.SupportedChars.Contains(chr))
                {
                    toReturn.Append(chr);
                    continue;
                }

                toReturn.Append('_');
            }

            var outStr = toReturn.ToString();
            //if(path != outStr)
            //{
            //    Console.WriteLine($"{path} -> {outStr}");
            //}
            return outStr;
        }

        private async Task<WorkItem> GetWorkItemCopyOrConvert(SyncConfig config, SourceFile sourceFile, string targetFilePath)
        {
            var sourceExtension = Path.GetExtension(sourceFile.Path);

            var supportedExtensionFormats = config.DeviceConfig.SupportedFormats.Where(x => x.Extension.Equals(sourceExtension, StringComparison.OrdinalIgnoreCase));
            if (supportedExtensionFormats.Any())
            {
                IMediaAnalysis mediaAnalysis;
                try
                {
                    mediaAnalysis = await FFProbe.AnalyseAsync(Path.Combine(config.SourceDir, sourceFile.Path));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while FFProbe {sourceFile.Path}");
                    return null;
                }

                var audioStream = mediaAnalysis.PrimaryAudioStream;
                if (audioStream != null && IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream.CodecName, audioStream.Profile))
                {
                    return new WorkItem
                    {
                        ActionType = ActionType.Copy,
                        SourceFile = sourceFile,
                        TargetFilePath = targetFilePath
                    };
                }
            }
            return new WorkItem
            {
                ActionType = ActionType.ConvertToFallback,
                SourceFile = sourceFile,
                TargetFilePath = Path.Combine(Path.GetDirectoryName(targetFilePath), Path.GetFileNameWithoutExtension(targetFilePath) + config.DeviceConfig.FallbackFormat.Extension)
            };
        }

        private bool IsSupported(IList<FileFormat> supportedFormats, string extension, string codec, string profile)
        {
            foreach (var format in supportedFormats)
            {
                if (format.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                    (format.Codec == null || format.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase)) &&
                    (format.Profile == null || format.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<OutputFile> HandleWorkItem(SyncConfig config, WorkItem workItem, ConcurrentBag<string> handledFiles)
        {
            try
            {
                OutputFile toReturn = null;
                switch (workItem.ActionType)
                {
                    case ActionType.Keep:
                        handledFiles.Add(workItem.TargetFilePath);
                        return null;
                    case ActionType.Copy:
                        {
                            Console.WriteLine($"--> Read {workItem.SourceFile.Path}");
                            handledFiles.Add(workItem.TargetFilePath);
                            toReturn = new OutputFile
                            {
                                Path = workItem.TargetFilePath,
                                ModifiedDate = workItem.SourceFile.ModifiedDate,
                                Content = File.ReadAllBytes(Path.Combine(config.SourceDir, workItem.SourceFile.Path))
                            };
                            Console.WriteLine($"<-- Read {workItem.SourceFile.Path}");
                            break;
                        }
                    case ActionType.ConvertToFallback:
                        {
                            Console.WriteLine($"--> Convert {workItem.SourceFile.Path}");
                            var fallbackCodec = config.DeviceConfig.FallbackFormat;
                            var tmpFileName = Path.GetTempFileName();
                            try
                            {
                                var args = FFMpegArguments
                                    .FromFileInput(Path.Combine(config.SourceDir, workItem.SourceFile.Path))
                                    .OutputToFile(tmpFileName, true, x =>
                                    {
                                        x
                                            .ForceFormat(fallbackCodec.Muxer)
                                            .WithAudioBitrate(fallbackCodec.Bitrate)
                                            .WithAudioCodec(fallbackCodec.EncoderCodec)
                                            .WithArgument(new CustomArgument("-map_metadata 0:s:0 -map_metadata 0:g")); // map flac and ogg tags to ID3

                                        if (!string.IsNullOrEmpty(fallbackCodec.CoverCodec))
                                        {
                                            x.WithVideoCodec(fallbackCodec.CoverCodec);
                                            if (fallbackCodec.MaxCoverSize != null)
                                            {
                                                x.WithArgument(new CustomArgument($"-vf \"scale='min({fallbackCodec.MaxCoverSize.Value},iw)':min'({fallbackCodec.MaxCoverSize.Value},ih)':force_original_aspect_ratio=decrease\""));
                                            }
                                        }
                                        else
                                        {
                                            x.DisableChannel(Channel.Video);
                                        }

                                        if (!string.IsNullOrEmpty(fallbackCodec.EncoderProfile))
                                        {
                                            x.WithArgument(new CustomArgument($"-profile:a {fallbackCodec.EncoderProfile}"));
                                        }
                                        if (!string.IsNullOrEmpty(fallbackCodec.AdditionalFlags))
                                        {
                                            x.WithArgument(new CustomArgument(fallbackCodec.AdditionalFlags));
                                        }
                                    });
                                await args
                                    //.NotifyOnProgress(x => Console.WriteLine($"{workItem.SourceFile.Path} {x.ToString()}"))
                                    .ProcessAsynchronously();
                                handledFiles.Add(workItem.TargetFilePath);
                                toReturn = new OutputFile
                                {
                                    Path = workItem.TargetFilePath,
                                    ModifiedDate = workItem.SourceFile.ModifiedDate,
                                    Content = File.ReadAllBytes(tmpFileName)
                                };
                            }
                            finally
                            {
                                File.Delete(tmpFileName);
                            }
                            Console.WriteLine($"<-- Convert {workItem.SourceFile.Path}");
                            break;
                        }
                    default:
                        break;
                }
                return toReturn;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        private async Task WriteFile(OutputFile file)
        {
            try
            {
                Console.WriteLine($"--> Write {file.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path));
                await File.WriteAllBytesAsync(file.Path, file.Content);
                File.SetLastWriteTime(file.Path, file.ModifiedDate);
                Console.WriteLine($"<-- Write {file.Path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }
    }
}
