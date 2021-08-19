using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

            var compareBlock = new TransformManyBlock<SourceFileInfo, ReadWorkItem>(async x => new ReadWorkItem[] { await CompareDates(config, x, cancellationToken) }.Where(y => y != null), workerOptions);
            var readBlock = new TransformBlock<ReadWorkItem, AnalyzeWorkItem>(async x => await Read(x, cancellationToken), readOptions);
            var analyzeBlock = new TransformManyBlock<AnalyzeWorkItem, ConvertWorkItem>(async x => new ConvertWorkItem[] { await Analyze(config, x) }.Where(y => y != null), workerOptions);
            var convertBlock = new TransformManyBlock<ConvertWorkItem, OutputFile>(async x => new OutputFile[] { await Convert(x, handledFiles, cancellationToken) }.Where(y => y != null), workerOptions);
            var writeBlock = new ActionBlock<OutputFile>(file => WriteFile(file, cancellationToken), writeOptions);

            compareBlock.LinkTo(readBlock, new DataflowLinkOptions { PropagateCompletion = true });
            readBlock.LinkTo(analyzeBlock, new DataflowLinkOptions { PropagateCompletion = true });
            analyzeBlock.LinkTo(convertBlock, new DataflowLinkOptions { PropagateCompletion = true });
            convertBlock.LinkTo(writeBlock, new DataflowLinkOptions { PropagateCompletion = true });

            // start pipeline by adding directories to check for changes
            var readDirsTask = ReadDirs(config, compareBlock, cancellationToken);

            // wait until last pipeline element is done
            await Task.WhenAll(readDirsTask, compareBlock.Completion, readBlock.Completion, analyzeBlock.Completion, convertBlock.Completion, writeBlock.Completion);

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

        private async Task<ReadWorkItem> CompareDates(SyncConfig config, SourceFileInfo sourceFile, CancellationToken cancellationToken)
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
            catch (Exception ex)
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
                    string tmpFilePath = GetTempFilePath();
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

        private async Task<ConvertWorkItem> Analyze(SyncConfig config, AnalyzeWorkItem workItem)
        {
            ConvertWorkItem toReturn;
            switch (workItem.ActionType)
            {
                case AnalyzeActionType.Keep:
                    toReturn = new ConvertWorkItem
                    {
                        ActionType = ConvertActionType.Keep,
                        SourceFileInfo = workItem.SourceFileInfo,
                        TargetFilePath = workItem.ExistingTargetFile
                    };
                    break;
                case AnalyzeActionType.CopyOrConvert:
                    Console.WriteLine($"--> Analyze {workItem.SourceFileInfo.AbsolutePath}");
                    toReturn = await GetWorkItemCopyOrConvert(config, workItem);
                    Console.WriteLine($"<-- Analyze {workItem.SourceFileInfo.AbsolutePath}");
                    break;
                default:
                    throw new ArgumentException("Invalid AnalyzeActionType");
            }
            return toReturn;
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

        private async Task<ConvertWorkItem> GetWorkItemCopyOrConvert(SyncConfig config, AnalyzeWorkItem workItem)
        {
            var sourceExtension = Path.GetExtension(workItem.SourceFileInfo.RelativePath);

            if (!config.SourceExtensions.Contains(sourceExtension))
            {
                return null;
            }

            IMediaAnalysis mediaAnalysis;
            try
            {
                mediaAnalysis = await FFProbe.AnalyseAsync(workItem.SourceTempFilePath ?? workItem.SourceFileInfo.AbsolutePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while FFProbe {workItem.SourceFileInfo.AbsolutePath}: {ex}");
                return null;
            }

            var tags = MapTags(mediaAnalysis, config.DeviceConfig.CharacterLimitations);

            var audioStream = mediaAnalysis.PrimaryAudioStream;

            if (audioStream == null)
            {
                Console.WriteLine($"Missing Audio stream: {workItem.SourceFileInfo.AbsolutePath}");
                return null;
            }

            var targetFilePath = Path.Combine(config.TargetDir, SanitizeText(config.DeviceConfig.CharacterLimitations, workItem.SourceFileInfo.RelativePath, true));

            if (IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream.CodecName, audioStream.Profile))
            {
                if (config.DeviceConfig.CharacterLimitations == null) // todo check tag / albumart compatibility?
                {
                    return new ConvertWorkItem
                    {
                        ActionType = ConvertActionType.Copy,
                        SourceFileInfo = workItem.SourceFileInfo,
                        SourceTempFilePath = workItem.SourceTempFilePath,
                        TargetFilePath = targetFilePath
                    };
                }
                else
                {
                    return new ConvertWorkItem
                    {
                        ActionType = ConvertActionType.Remux,
                        SourceFileInfo = workItem.SourceFileInfo,
                        SourceTempFilePath = workItem.SourceTempFilePath,
                        TargetFilePath = targetFilePath,
                        EncoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension),
                        Tags = tags
                    };
                }
            }

            return new ConvertWorkItem
            {
                ActionType = ConvertActionType.Transcode,
                SourceFileInfo = workItem.SourceFileInfo,
                SourceTempFilePath = workItem.SourceTempFilePath,
                TargetFilePath = Path.ChangeExtension(targetFilePath, config.DeviceConfig.FallbackFormat.Extension),
                EncoderInfo = config.DeviceConfig.FallbackFormat,
                Tags = tags
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

        private async Task<OutputFile> Convert(ConvertWorkItem workItem, ConcurrentBag<string> handledFiles, CancellationToken cancellationToken)
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
                            var outputFormat = workItem.EncoderInfo;
                            var outFilePath = GetTempFilePath();
                            try
                            {

                                var args = FFMpegArguments
                                    //.FromPipeInput(new StreamPipeSource(inputMs))
                                    .FromFileInput(workItem.SourceTempFilePath ?? workItem.SourceFileInfo.AbsolutePath)
                                    .OutputToFile(outFilePath, true, x => // we do not use pipe output here because ffmpeg can't write the header correctly when we use streams
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
                                        foreach (var tag in workItem.Tags)
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
                                    //.NotifyOnProgress(x => Console.WriteLine($"{workItem.SourceFileInfo.AbsolutePath} {x.ToString()}"))
                                    .ProcessAsynchronously();
                                cancellationToken.ThrowIfCancellationRequested();
                                handledFiles.Add(workItem.TargetFilePath);
                                toReturn = new OutputFile
                                {
                                    Path = workItem.TargetFilePath,
                                    ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                                    Content = File.ReadAllBytes(outFilePath)
                                };
                            }
                            finally
                            {
                                File.Delete(outFilePath);
                            }
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

        private string GetTempFilePath()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "MusicSync");
            Directory.CreateDirectory(tmpDir);
            return Path.Combine(tmpDir, $"{Guid.NewGuid():D}.tmp");
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
