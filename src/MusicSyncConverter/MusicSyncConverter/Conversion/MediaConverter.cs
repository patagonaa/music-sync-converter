using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using MusicSyncConverter.Config;
using MusicSyncConverter.Tags;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Conversion
{
    class MediaConverter
    {
        private readonly TextSanitizer _sanitizer;
        private readonly PathMatcher _pathMatcher;
        private readonly List<ITagReader> _tagReaders;
        private readonly List<ITagWriter> _tagWriters;
        private readonly FfmpegTagReader _ffmpegTagReader;
        private readonly ITempFileSession _tempFileSession;

        public MediaConverter(ITempFileSession tempFileSession)
        {
            _sanitizer = new TextSanitizer();
            _pathMatcher = new PathMatcher();
            _tagReaders = new List<ITagReader> { new MetaFlacReaderWriter(tempFileSession), new VorbisCommentReaderWriter(tempFileSession) };
            _tagWriters = new List<ITagWriter> { new MetaFlacReaderWriter(tempFileSession), new VorbisCommentReaderWriter(tempFileSession) };
            _ffmpegTagReader = new FfmpegTagReader();
            _tempFileSession = tempFileSession;
        }

        public async Task<(string OutputFile, string OutputExtension)> RemuxOrConvert(SyncConfig config, string inputFile, string originalFilePath, string? albumArtPath, IProducerConsumerCollection<string> infoLogMessages, CancellationToken cancellationToken)
        {
            var sourceExtension = Path.GetExtension(originalFilePath);

            if (!config.SourceExtensions.Contains(sourceExtension, StringComparer.OrdinalIgnoreCase))
            {
                throw new Exception("Cannot convert files not in SourceExtensions");
            }

            IMediaAnalysis mediaAnalysis;
            try
            {
                mediaAnalysis = await FFProbe.AnalyseAsync(inputFile);
            }
            catch (Exception ex)
            {
                throw new Exception("Error during FFProbe: " + ex);
            }

            var rawTags = await (_tagReaders.FirstOrDefault(x => x.CanHandle(inputFile, sourceExtension)) ?? _ffmpegTagReader).GetTags(mediaAnalysis, inputFile, cancellationToken);

            var sanitizedTags = SanitizeTags(rawTags, originalFilePath, config.DeviceConfig.TagCharacterLimitations, config.DeviceConfig.TagValueDelimiter, infoLogMessages);
            var ffmpegTags = MapToFfmpegTags(sanitizedTags);

            var audioStream = mediaAnalysis.PrimaryAudioStream;

            if (audioStream == null)
            {
                throw new Exception("Missing Audio stream");
            }

            EncoderInfo encoderInfo;

            var overrides = (config.PathFormatOverrides ?? Enumerable.Empty<KeyValuePair<string, FileFormatOverride>>()).Where(x => _pathMatcher.Matches(x.Key, originalFilePath, false)).Select(x => x.Value).ToList();

            var mergedOverrides = overrides.Any() ? MergeOverrides(overrides) : null;

            if ((mergedOverrides == null || IsWithinLimitations(mergedOverrides, sourceExtension, audioStream)) && IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream))
            {
                encoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension, config.DeviceConfig.FallbackFormat);
            }
            else if (mergedOverrides != null)
            {
                encoderInfo = GetEncoderInfoOverride(config.DeviceConfig.FallbackFormat, mergedOverrides);
            }
            else
            {
                encoderInfo = config.DeviceConfig.FallbackFormat;
            }

            var outputFile = _tempFileSession.GetTempFilePath();
            await Convert(inputFile, mediaAnalysis.PrimaryVideoStream != null, albumArtPath, outputFile, encoderInfo, ffmpegTags, cancellationToken);

            var tagWriter = _tagWriters.FirstOrDefault(x => x.CanHandle(outputFile, encoderInfo.Extension));
            if (tagWriter != null)
                await tagWriter.SetTags(sanitizedTags, outputFile, cancellationToken);

            return (outputFile, encoderInfo.Extension);
        }

        private EncoderInfo GetEncoderInfoOverride(EncoderInfo fallbackFormat, FileFormatOverride toApply)
        {
            var toReturn = fallbackFormat.Clone();
            if (toApply.Extension != null && toApply.Extension != toReturn.Extension)
            {
                toReturn.Extension = toApply.Extension;
            }
            if (toApply.Codec != null && toApply.Codec != toReturn.Codec)
            {
                toReturn.Codec = toApply.Codec;
                toReturn.Profile = null;
            }
            if (toApply.Profile != null && toApply.Profile != toReturn.Profile)
            {
                toReturn.Profile = toApply.Profile;
            }
            if (toApply.Muxer != null && toApply.Muxer != toReturn.Muxer)
            {
                toReturn.Muxer = toApply.Muxer;
            }
            if (toApply.MaxChannels != null && (toReturn.Channels == null || toReturn.Channels > toApply.MaxChannels))
                toReturn.Channels = toApply.MaxChannels;

            if (toApply.MaxSampleRateHz != null && (toReturn.SampleRateHz == null || toReturn.SampleRateHz > toApply.MaxSampleRateHz))
                toReturn.SampleRateHz = toApply.MaxSampleRateHz;

            if (toApply.MaxBitrate != null && (toReturn.Bitrate == null || toReturn.Bitrate > toApply.MaxBitrate))
                toReturn.Bitrate = toApply.MaxBitrate;

            return toReturn;
        }

        private FileFormatOverride MergeOverrides(IReadOnlyList<FileFormatOverride> overrides)
        {
            var toReturn = overrides[0].Clone();

            foreach (var toApply in overrides.Skip(1))
            {
                if (toApply.Extension != null)
                {
                    if (toReturn.Extension != null && toReturn.Extension != toApply.Extension)
                    {
                        throw new InvalidOperationException("Multiple extension limitations can't be merged");
                    }
                    toReturn.Extension = toApply.Extension;
                }

                if (toApply.Codec != null)
                {
                    if (toReturn.Codec != null && toReturn.Codec != toApply.Codec)
                    {
                        throw new InvalidOperationException("Multiple codec limitations can't be merged");
                    }
                    toReturn.Codec = toApply.Codec;
                }

                if (toApply.Profile != null)
                {
                    if (toReturn.Profile != null && toReturn.Profile != toApply.Profile)
                    {
                        throw new InvalidOperationException("Multiple profile limitations can't be merged");
                    }
                    toReturn.Profile = toApply.Profile;
                }

                if (toApply.Muxer != null)
                {
                    if (toReturn.Muxer != null && toReturn.Muxer != toApply.Muxer)
                    {
                        throw new InvalidOperationException("Multiple muxer limitations can't be merged");
                    }
                    toReturn.Muxer = toApply.Muxer;
                }

                if (toApply.MaxChannels != null && (toReturn.MaxChannels == null || toReturn.MaxChannels > toApply.MaxChannels))
                    toReturn.MaxChannels = toApply.MaxChannels;

                if (toApply.MaxSampleRateHz != null && (toReturn.MaxSampleRateHz == null || toReturn.MaxSampleRateHz > toApply.MaxSampleRateHz))
                    toReturn.MaxSampleRateHz = toApply.MaxSampleRateHz;

                if (toApply.MaxBitrate != null && (toReturn.MaxBitrate == null || toReturn.MaxBitrate > toApply.MaxBitrate))
                    toReturn.MaxBitrate = toApply.MaxBitrate;
            }

            return toReturn;
        }

        private EncoderInfo GetEncoderInfoRemux(IMediaAnalysis mediaAnalysis, string sourceExtension, EncoderInfo fallbackFormat)
        {
            // this is pretty dumb, but the muxer ffprobe spits out and the one that ffmpeg needs are different
            // also, ffprobe sometimes misdetects files, so we're just going by file ending here while we can
            switch (sourceExtension.ToLowerInvariant())
            {
                case ".m4a":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "ipod",
                        AdditionalFlags = "-movflags faststart",
                        CoverCodec = fallbackFormat.CoverCodec,
                        MaxCoverSize = fallbackFormat.MaxCoverSize,
                        Extension = sourceExtension
                    };
                case ".aac":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "adts",
                        CoverCodec = null,
                        Extension = sourceExtension
                    };
                case ".wma":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "asf",
                        CoverCodec = fallbackFormat.CoverCodec,
                        MaxCoverSize = fallbackFormat.MaxCoverSize,
                        Extension = sourceExtension
                    };
                case ".ogg":
                case ".opus":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "ogg",
                        CoverCodec = fallbackFormat.CoverCodec,
                        MaxCoverSize = fallbackFormat.MaxCoverSize,
                        Extension = sourceExtension
                    };
                case ".mp3":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "mp3",
                        CoverCodec = fallbackFormat.CoverCodec,
                        MaxCoverSize = fallbackFormat.MaxCoverSize,
                        Extension = sourceExtension
                    };
                case ".flac":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "flac",
                        CoverCodec = fallbackFormat.CoverCodec,
                        MaxCoverSize = fallbackFormat.MaxCoverSize,
                        Extension = sourceExtension
                    };
                case ".wav":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "wav",
                        CoverCodec = null,
                        Extension = sourceExtension
                    };

                default:
                    throw new ArgumentException($"don't know how to remux {sourceExtension} ({mediaAnalysis.Format.FormatName})");
            }
        }



        private IReadOnlyList<KeyValuePair<string, string>> SanitizeTags(IReadOnlyList<KeyValuePair<string, string>> tags, string originalPath, CharacterLimitations? characterLimitations, string? tagValueDelimiter, IProducerConsumerCollection<string> infoLogMessages)
        {
            var toReturn = new List<KeyValuePair<string, string>>();
            if (tagValueDelimiter != null)
            {
                tags = tags
                    .GroupBy(tag => tag.Key)
                    .Select(tagGroup => new KeyValuePair<string, string>(tagGroup.Key, string.Join(tagValueDelimiter, tagGroup.Select(tag => tag.Value))))
                    .ToList();
            }

            foreach (var tag in tags)
            {
                var tagValue = _sanitizer.SanitizeText(characterLimitations, tag.Value, false, out var hasUnsupportedChars);
                if (hasUnsupportedChars)
                    infoLogMessages.TryAdd(GetUnsupportedStringsMessage(originalPath, tag.Value));
                toReturn.Add(new KeyValuePair<string, string>(tag.Key, tagValue));
            }

            return toReturn;
        }

        private Dictionary<string, string> MapToFfmpegTags(IReadOnlyList<KeyValuePair<string, string>> tags)
        {
            var toReturn = new Dictionary<string, string>();
            foreach (var tag in tags)
            {
                var ffmpegKey = FfmpegTagKeyMapping.GetFfmpegKey(tag.Key);
                if (ffmpegKey == null)
                {
                    //TODO: Log
                    continue;
                }

                if (toReturn.TryGetValue(ffmpegKey, out var existingValue))
                {
                    toReturn[ffmpegKey] = $"{existingValue};{tag.Value}";
                }
                else
                {
                    toReturn[ffmpegKey] = tag.Value;
                }
            }

            return toReturn;
        }

        private string GetUnsupportedStringsMessage(string path, string str)
        {
            return $"Unsupported chars in {path}: {str}";
        }

        private bool IsSupported(IList<FileFormatLimitation> supportedFormats, string sourceExtension, AudioStream audioStream)
        {
            foreach (var supportedFormat in supportedFormats)
            {
                if (IsWithinLimitations(supportedFormat, sourceExtension, audioStream))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsWithinLimitations(FileFormatLimitation limitation, string sourceExtension, AudioStream audioStream)
        {
            return (limitation.Extension == null || limitation.Extension.Equals(sourceExtension, StringComparison.OrdinalIgnoreCase)) &&
                                (limitation.Codec == null || limitation.Codec.Equals(audioStream.CodecName, StringComparison.OrdinalIgnoreCase)) &&
                                (limitation.Profile == null || limitation.Profile.Equals(audioStream.Profile, StringComparison.OrdinalIgnoreCase)) &&
                                (limitation.MaxChannels == null || limitation.MaxChannels >= audioStream.Channels) &&
                                (limitation.MaxSampleRateHz == null || limitation.MaxSampleRateHz >= audioStream.SampleRateHz) &&
                                (limitation.MaxBitrate == null || limitation.MaxBitrate >= audioStream.BitRate / 1000);
        }

        private async Task<string> Convert(string sourcePath, bool hasEmbeddedCover, string? externalCoverPath, string outFilePath, EncoderInfo encoderInfo, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            var args = FFMpegArguments
                .FromFileInput(sourcePath);
            var hasExternalCover = encoderInfo.CoverCodec != null && externalCoverPath != null;
            if (hasExternalCover)
            {
                args.AddFileInput(externalCoverPath!);
            }

            var argsProcessor = args.OutputToFile(outFilePath, true, x => // we do not use pipe output here because ffmpeg can't write the header correctly when we use streams
            {
                x.WithArgument(new CustomArgument("-map 0:a"));

                if (hasEmbeddedCover)
                {
                    x.WithArgument(new CustomArgument("-map 0:v -disposition:v attached_pic"));
                }
                else if (hasExternalCover)
                {
                    x.WithArgument(new CustomArgument("-map 1:v -disposition:v attached_pic"));
                }

                x.WithAudioCodec(encoderInfo.Codec);

                if (encoderInfo.Channels.HasValue)
                {
                    x.WithArgument(new CustomArgument($"-ac {encoderInfo.Channels.Value}"));
                }

                if (encoderInfo.SampleRateHz.HasValue)
                {
                    x.WithAudioSamplingRate(encoderInfo.SampleRateHz.Value);
                }

                if (encoderInfo.Bitrate.HasValue)
                {
                    x.WithAudioBitrate(encoderInfo.Bitrate.Value);
                }

                if (!string.IsNullOrEmpty(encoderInfo.Profile))
                {
                    x.WithArgument(new CustomArgument($"-profile:a {encoderInfo.Profile}"));
                }
                x.WithoutMetadata();
                foreach (var tag in tags)
                {
                    x.WithArgument(new CustomArgument($"-metadata {tag.Key}=\"{tag.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));
                }

                if (!string.IsNullOrEmpty(encoderInfo.CoverCodec))
                {
                    x.WithVideoCodec(encoderInfo.CoverCodec);
                    if (encoderInfo.MaxCoverSize != null)
                    {
                        x.WithArgument(new CustomArgument($"-vf \"scale='min({encoderInfo.MaxCoverSize.Value},iw)':min'({encoderInfo.MaxCoverSize.Value},ih)':force_original_aspect_ratio=decrease\""));
                    }
                }
                else
                {
                    x.DisableChannel(Channel.Video);
                }

                x.ForceFormat(encoderInfo.Muxer);

                if (!string.IsNullOrEmpty(encoderInfo.AdditionalFlags))
                {
                    x.WithArgument(new CustomArgument(encoderInfo.AdditionalFlags));
                }
            });
            argsProcessor.CancellableThrough(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await argsProcessor
                //.NotifyOnProgress(x => Console.WriteLine($"{workItem.SourceFileInfo.AbsolutePath} {x.ToString()}"))
                .ProcessAsynchronously();
            cancellationToken.ThrowIfCancellationRequested();
            return outFilePath;
        }
    }
}
