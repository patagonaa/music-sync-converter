using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    class SyncService
    {
        private static readonly IReadOnlyList<string> _supportedTags = new List<string>
        {
            "album",
            "title",
            "composer",
            "genre",
            "track",
            "disc",
            "date",
            "artist",
            "album_artist",
            "comment"
        };

        public async Task Run(SyncConfig config, CancellationToken cancellationToken)
        {
            //set up pipeline
            var workerOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 16,
                MaxDegreeOfParallelism = config.WorkersConvert,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var writeOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 8,
                MaxDegreeOfParallelism = config.WorkersWrite,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            };

            var compareBlock = new TransformManyBlock<SourceFile, WorkItem>(async x => new WorkItem[] { await Compare(config, x) }.Where(y => y != null), workerOptions);

            var handledFiles = new ConcurrentBag<string>();
            var handleBlock = new TransformManyBlock<WorkItem, OutputFile>(async x => new OutputFile[] { await HandleWorkItem(config, x, handledFiles, cancellationToken) }.Where(y => y != null), workerOptions);

            compareBlock.LinkTo(handleBlock, new DataflowLinkOptions { PropagateCompletion = true });

            var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, cancellationToken), writeOptions);

            handleBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = true });

            // start pipeline by adding directories to check for changes
            var readDirsTask = ReadDirs(config, compareBlock, cancellationToken);

            // wait until last pipeline element is done
            await Task.WhenAll(readDirsTask, compareBlock.Completion, handleBlock.Completion, writeBlock.Completion);

            // delete additional files and empty directories
            DeleteAdditionalFiles(config, handledFiles);
            DeleteEmptySubdirectories(config.TargetDir);
        }

        private async Task ReadDirs(SyncConfig config, ITargetBlock<SourceFile> files, CancellationToken cancellationToken)
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

        private async Task ReadDir(SyncConfig config, DirectoryInfo dir, ITargetBlock<SourceFile> files, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirLength = config.SourceDir.TrimEnd(Path.DirectorySeparatorChar).Length + 1;

            if (config.Exclude.Contains(dir.FullName.Substring(sourceDirLength)))
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
                    await files.SendAsync(new SourceFile
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

        private async Task<WorkItem> Compare(SyncConfig config, SourceFile sourceFile)
        {
            try
            {
                var targetFilePath = Path.Combine(config.TargetDir, SanitizeText(config.DeviceConfig.CharacterLimitations, sourceFile.RelativePath, true));
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
                            ActionType = ActionType.None,
                            SourceFileInfo = sourceFile,
                            TargetFileInfo = new TargetFileInfo(targetInfo[0].FullName)
                        };
                    }
                }
                else
                {
                    Console.WriteLine($"Multiple potential existing target files for {sourceFile.RelativePath}");
                }
                return null;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private string SanitizeText(CharacterLimitations config, string text, bool isPath)
        {
            if (config == null)
                return text;
            var toReturn = new StringBuilder();

            var unsupportedChars = false;

            foreach (var chr in text)
            {
                if (isPath && (chr == Path.DirectorySeparatorChar || chr == '.'))
                {
                    toReturn.Append(chr);
                    continue;
                }

                var replacement = config.Replacements?.FirstOrDefault(x => x.Char == chr);
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

                unsupportedChars = true;
                toReturn.Append(chr);
            }

            if (unsupportedChars)
            {
                Console.WriteLine($"Warning: unsupported chars in {text}");
            }

            return toReturn.ToString();
        }

        private async Task<WorkItem> GetWorkItemCopyOrConvert(SyncConfig config, SourceFile sourceFile, string targetFilePath)
        {
            var sourceExtension = Path.GetExtension(sourceFile.RelativePath);

            if (!config.SourceExtensions.Contains(sourceExtension))
            {
                return null;
            }

            IMediaAnalysis mediaAnalysis;
            try
            {
                mediaAnalysis = await FFProbe.AnalyseAsync(sourceFile.AbsolutePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while FFProbe {sourceFile.AbsolutePath}: {ex}");
                return null;
            }

            var tags = MapTags(mediaAnalysis, config.DeviceConfig.CharacterLimitations);

            var audioStream = mediaAnalysis.PrimaryAudioStream;

            if (audioStream == null)
            {
                Console.WriteLine($"Missing Audio stream: {sourceFile.AbsolutePath}");
                return null;
            }

            if (IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream.CodecName, audioStream.Profile))
            {
                if (config.DeviceConfig.CharacterLimitations == null) // todo check specific tag compatibility?
                {
                    return new WorkItem
                    {
                        ActionType = ActionType.Copy,
                        SourceFileInfo = sourceFile,
                        TargetFileInfo = new TargetFileInfo(targetFilePath)
                    };
                }
                else
                {
                    return new WorkItem
                    {
                        ActionType = ActionType.Remux,
                        SourceFileInfo = sourceFile,
                        TargetFileInfo = new TargetFileInfo(targetFilePath)
                        {
                            EncoderInfo = GetEncoderInfoRemux(mediaAnalysis),
                            Tags = tags
                        }
                    };
                }
            }

            return new WorkItem
            {
                ActionType = ActionType.Transcode,
                SourceFileInfo = sourceFile,
                TargetFileInfo = new TargetFileInfo(Path.ChangeExtension(targetFilePath, config.DeviceConfig.FallbackFormat.Extension))
                {
                    EncoderInfo = config.DeviceConfig.FallbackFormat,
                    Tags = tags
                }
            };
        }

        private EncoderInfo GetEncoderInfoRemux(IMediaAnalysis mediaAnalysis)
        {
            switch (mediaAnalysis.Format.FormatName)
            {
                case "mov,mp4,m4a,3gp,3g2,mj2":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "ipod",
                        AdditionalFlags = "-movflags faststart",
                        CoverCodec = "copy",
                        Extension = ".m4a"
                    };
                case "mp3":
                case "flac":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = mediaAnalysis.Format.FormatName,
                        CoverCodec = "copy",
                        Extension = "." + mediaAnalysis.Format.FormatName
                    };
                default:
                    throw new ArgumentException($"don't know how to remux {mediaAnalysis.Format.FormatName}");
            }
        }

        private Dictionary<string, string> MapTags(IMediaAnalysis mediaAnalysis, CharacterLimitations characterLimitations)
        {
            var toReturn = new Dictionary<string, string>();
            foreach (var tag in mediaAnalysis.Format.Tags ?? new Dictionary<string, string>())
            {
                if (!_supportedTags.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                toReturn[tag.Key] = SanitizeText(characterLimitations, tag.Value, false);
            }
            foreach (var tag in mediaAnalysis.PrimaryAudioStream.Tags ?? new Dictionary<string, string>())
            {
                if (!_supportedTags.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                toReturn[tag.Key] = SanitizeText(characterLimitations, tag.Value, false);
            }

            return toReturn;
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

        private async Task<OutputFile> HandleWorkItem(SyncConfig config, WorkItem workItem, ConcurrentBag<string> handledFiles, CancellationToken cancellationToken)
        {
            try
            {
                OutputFile toReturn = null;
                switch (workItem.ActionType)
                {
                    case ActionType.None:
                        handledFiles.Add(workItem.TargetFileInfo.AbsolutePath);
                        return null;
                    case ActionType.Copy:
                        {
                            Console.WriteLine($"--> Read {workItem.SourceFileInfo.RelativePath}");
                            handledFiles.Add(workItem.TargetFileInfo.AbsolutePath);
                            toReturn = new OutputFile
                            {
                                Path = workItem.TargetFileInfo.AbsolutePath,
                                ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                Content = await File.ReadAllBytesAsync(workItem.SourceFileInfo.AbsolutePath, cancellationToken)
                            };
                            Console.WriteLine($"<-- Read {workItem.SourceFileInfo.RelativePath}");
                            break;
                        }
                    case ActionType.Transcode:
                    case ActionType.Remux:
                        {
                            Console.WriteLine($"--> {workItem.ActionType} {workItem.SourceFileInfo.RelativePath}");
                            var outputFormat = workItem.TargetFileInfo.EncoderInfo;
                            var tmpFileName = Path.Combine(Path.GetTempPath(), $"MusicSync.{Guid.NewGuid():D}.tmp");
                            try
                            {
                                var args = FFMpegArguments
                                    .FromFileInput(workItem.SourceFileInfo.AbsolutePath)
                                    .OutputToFile(tmpFileName, true, x =>
                                    {
                                        x.WithAudioCodec(outputFormat.Codec);

                                        if (outputFormat.Bitrate.HasValue)
                                        {
                                            x.WithAudioBitrate(outputFormat.Bitrate.Value);
                                        }

                                        if (!string.IsNullOrEmpty(outputFormat.Profile))
                                        {
                                            x.WithArgument(new CustomArgument($"-profile:a {outputFormat.Profile}"));
                                        }

                                        foreach (var tag in workItem.TargetFileInfo.Tags)
                                        {
                                            x.WithArgument(new CustomArgument($"-metadata {tag.Key}=\"{tag.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));
                                        }

                                        if (!string.IsNullOrEmpty(outputFormat.CoverCodec))
                                        {
                                            x.WithVideoCodec(outputFormat.CoverCodec);
                                            if (outputFormat.MaxCoverSize != null)
                                            {
                                                x.WithArgument(new CustomArgument($"-vf \"scale='min({outputFormat.MaxCoverSize.Value},iw)':min'({outputFormat.MaxCoverSize.Value},ih)':force_original_aspect_ratio=decrease\""));
                                            }
                                        }
                                        else
                                        {
                                            x.DisableChannel(Channel.Video);
                                        }

                                        x.ForceFormat(outputFormat.Muxer);

                                        if (!string.IsNullOrEmpty(outputFormat.AdditionalFlags))
                                        {
                                            x.WithArgument(new CustomArgument(outputFormat.AdditionalFlags));
                                        }
                                    })
                                    .CancellableThrough(out var cancelFfmpeg);
                                cancellationToken.Register(() => cancelFfmpeg());
                                cancellationToken.ThrowIfCancellationRequested();
                                await args
                                    //.NotifyOnProgress(x => Console.WriteLine($"{workItem.SourceFile.Path} {x.ToString()}"))
                                    .ProcessAsynchronously();
                                handledFiles.Add(workItem.TargetFileInfo.AbsolutePath);
                                toReturn = new OutputFile
                                {
                                    Path = workItem.TargetFileInfo.AbsolutePath,
                                    ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                    Content = File.ReadAllBytes(tmpFileName)
                                };
                            }
                            finally
                            {
                                File.Delete(tmpFileName);
                            }
                            Console.WriteLine($"<-- {workItem.ActionType} {workItem.SourceFileInfo.RelativePath}");
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
            return null;
        }

        private async Task WriteFile(OutputFile file, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"--> Write {file.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path));
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
