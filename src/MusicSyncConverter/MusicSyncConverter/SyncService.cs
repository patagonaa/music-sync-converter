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
            var handleBlock = new TransformManyBlock<WorkItem, OutputFile>(async x => new OutputFile[] { await HandleWorkItem(x, handledFiles, cancellationToken) }.Where(y => y != null), workerOptions);

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
                var targetInfos = directoryInfo.Exists ? directoryInfo.GetFiles($"{Path.GetFileNameWithoutExtension(targetFilePath)}.*") : new FileInfo[0];

                if (targetInfos.Length != 1) // zero or multiple target files
                {
                    // if there are multiple target files, delete them
                    foreach (var targetInfo in targetInfos)
                    {
                        Console.WriteLine($"Deleting ambiguous file {targetInfo.FullName}");
                        targetInfo.Delete();
                    }
                    return await GetWorkItemCopyOrConvert(config, sourceFile, targetFilePath);
                }

                // only one target item, so check if it is up to date
                var targetDate = targetInfos[0].LastWriteTime;
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
                        TargetFileInfo = new TargetFileInfo(targetInfos[0].FullName)
                    };
                }
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

            var pathUnsupportedChars = Path.GetInvalidFileNameChars();

            var unsupportedChars = false;

            foreach (var inChar in text)
            {
                // path seperator always allowed for path
                if (isPath && (inChar == Path.DirectorySeparatorChar || inChar == '.'))
                {
                    toReturn.Append(inChar);
                    continue;
                }

                string toInsert;

                var replacement = config.Replacements?.FirstOrDefault(x => x.Char == inChar);
                if (replacement != null)
                {
                    toInsert = replacement.Replacement;
                }
                else
                {
                    toInsert = inChar.ToString();
                }

                foreach (var outChar in toInsert)
                {

                    // if this is a path, replace chars that are invalid for path names
                    if (isPath && pathUnsupportedChars.Contains(outChar))
                    {
                        unsupportedChars = true;
                        toReturn.Append('_');
                    }
                    else if (!config.SupportedChars.Contains(outChar))
                    {
                        // we just accept our faith and insert the character anyways
                        unsupportedChars = true;
                        toReturn.Append(outChar);
                    }
                    else
                    {
                        // char is supported
                        toReturn.Append(outChar);
                    }
                }
            }

            if (unsupportedChars)
            {
                //Console.WriteLine($"Warning: unsupported chars in {text}");
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
                if (config.DeviceConfig.CharacterLimitations == null) // todo check tag / albumart compatibility?
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
                            EncoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension),
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

        private EncoderInfo GetEncoderInfoRemux(IMediaAnalysis mediaAnalysis, string sourceExtension)
        {
            // this is pretty dumb, but the muxer ffprobe spits out and the one that ffmpeg needs are different
            // also, ffprobe sometimes misdetects files, so we're just going by file ending here while we can
            switch (sourceExtension)
            {
                case ".m4a":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "ipod",
                        AdditionalFlags = "-movflags faststart",
                        CoverCodec = "copy",
                        Extension = sourceExtension
                    };
                case ".wma":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "asf",
                        CoverCodec = "copy",
                        Extension = sourceExtension
                    };
                case ".ogg":
                case ".opus":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "ogg",
                        CoverCodec = "copy",
                        Extension = sourceExtension
                    };
                case ".mp3":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "mp3",
                        CoverCodec = "copy",
                        Extension = sourceExtension
                    };
                case ".flac":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "flac",
                        CoverCodec = "copy",
                        Extension = sourceExtension
                    };
                case ".wav":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "wav",
                        CoverCodec = "copy",
                        Extension = sourceExtension
                    };

                default:
                    throw new ArgumentException($"don't know how to remux {sourceExtension} ({mediaAnalysis.Format.FormatName})");
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

        private async Task<OutputFile> HandleWorkItem(WorkItem workItem, ConcurrentBag<string> handledFiles, CancellationToken cancellationToken)
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
                            var tmpDir = Path.Combine(Path.GetTempPath(), "MusicSync");
                            Directory.CreateDirectory(tmpDir);
                            var tmpFileName = Path.Combine(tmpDir, $"{Guid.NewGuid():D}.tmp");
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
                                        x.WithoutMetadata();
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
                                    .CancellableThrough(cancellationToken);
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
