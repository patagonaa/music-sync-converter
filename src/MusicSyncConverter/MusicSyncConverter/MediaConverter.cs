﻿using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class MediaConverter
    {
        public MediaConverter()
        {
            _sanitizer = new TextSanitizer();
            _pathMatcher = new PathMatcher();
        }

        private readonly TextSanitizer _sanitizer;
        private readonly PathMatcher _pathMatcher;

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

        public async Task<(string OutputFile, string OutputExtension)> RemuxOrConvert(SyncConfig config, string inputFile, string originalFilePath, string? albumArtPath, string outputFile, IProducerConsumerCollection<string> infoLogMessages, CancellationToken cancellationToken)
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

            var tags = MapTags(mediaAnalysis, originalFilePath, config.DeviceConfig.CharacterLimitations, infoLogMessages);

            var audioStream = mediaAnalysis.PrimaryAudioStream;

            if (audioStream == null)
            {
                throw new Exception("Missing Audio stream");
            }

            EncoderInfo encoderInfo;

            var overrides = (config.PathFormatOverrides ?? Enumerable.Empty<KeyValuePair<string, FileFormatLimitation>>()).Where(x => _pathMatcher.Matches(x.Key, originalFilePath, false)).Select(x => x.Value).ToList();

            if (overrides.Any())
            {
                var mergedOverrides = MergeLimitations(overrides);
                encoderInfo = ApplyLimitation(config.DeviceConfig.FallbackFormat, mergedOverrides);
            }
            else if (IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream))
            {
                encoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension, config.DeviceConfig.FallbackFormat);
            }
            else
            {
                encoderInfo = config.DeviceConfig.FallbackFormat;
            }

            await Convert(inputFile, mediaAnalysis.PrimaryVideoStream != null, albumArtPath, outputFile, encoderInfo, tags, cancellationToken);
            return (outputFile, encoderInfo.Extension);
        }

        private EncoderInfo ApplyLimitation(EncoderInfo fallbackFormat, FileFormatLimitation toApply)
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
            if (toApply.MaxChannels != null && (toReturn.Channels == null || toReturn.Channels > toApply.MaxChannels))
                toReturn.Channels = toApply.MaxChannels;

            if (toApply.MaxSampleRateHz != null && (toReturn.SampleRateHz == null || toReturn.SampleRateHz > toApply.MaxSampleRateHz))
                toReturn.SampleRateHz = toApply.MaxSampleRateHz;

            if (toApply.MaxBitrate != null && (toReturn.Bitrate == null || toReturn.Bitrate > toApply.MaxBitrate))
                toReturn.Bitrate = toApply.MaxBitrate;

            return toReturn;
        }

        private FileFormatLimitation MergeLimitations(IReadOnlyList<FileFormatLimitation> overrides)
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

        private Dictionary<string, string> MapTags(IMediaAnalysis mediaAnalysis, string relativePath, CharacterLimitations? characterLimitations, IProducerConsumerCollection<string> infoLogMessages)
        {
            var toReturn = new Dictionary<string, string>();
            foreach (var tag in mediaAnalysis.Format.Tags ?? new Dictionary<string, string>())
            {
                if (!_supportedTags.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                toReturn[tag.Key] = _sanitizer.SanitizeText(characterLimitations, tag.Value, false, out var hasUnsupportedChars);
                if (hasUnsupportedChars)
                    infoLogMessages.TryAdd(GetUnsupportedStringsMessage(relativePath, tag.Value));
            }
            foreach (var tag in mediaAnalysis.PrimaryAudioStream?.Tags ?? new Dictionary<string, string>())
            {
                if (!_supportedTags.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                toReturn[tag.Key] = _sanitizer.SanitizeText(characterLimitations, tag.Value, false, out var hasUnsupportedChars);
                if (hasUnsupportedChars)
                    infoLogMessages.TryAdd(GetUnsupportedStringsMessage(relativePath, tag.Value));
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
                                (limitation.MaxBitrate == null || limitation.MaxBitrate >= (audioStream.BitRate / 1000));
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
