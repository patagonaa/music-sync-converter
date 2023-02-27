﻿using Microsoft.Extensions.Logging;
using MusicSyncConverter.Config;
using MusicSyncConverter.Conversion.Ffmpeg;
using MusicSyncConverter.Tags;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Conversion
{
    class MediaConverter
    {
        private readonly ITextSanitizer _sanitizer;
        private readonly PathMatcher _pathMatcher;
        private readonly List<ITagReader> _tagReaders;
        private readonly List<ITagWriter> _tagWriters;
        private readonly FfmpegTagReader _ffmpegTagReader;
        private readonly FfmpegTagMapper _ffmpegTagMapper;
        private readonly ITempFileSession _tempFileSession;
        private readonly ILogger _logger;

        public MediaConverter(ITempFileSession tempFileSession, ILogger logger)
        {
            _sanitizer = new TextSanitizer();
            _pathMatcher = new PathMatcher();
            _tagReaders = new List<ITagReader> { new MetaFlacReaderWriter(tempFileSession), new VorbisCommentReaderWriter(tempFileSession) };
            _tagWriters = new List<ITagWriter> { new MetaFlacReaderWriter(tempFileSession), new VorbisCommentReaderWriter(tempFileSession) };
            _ffmpegTagReader = new FfmpegTagReader(logger);
            _ffmpegTagMapper = new FfmpegTagMapper(logger);
            _tempFileSession = tempFileSession;
            _logger = logger;
        }

        public async Task<(string OutputFile, string OutputExtension)> RemuxOrConvert(SyncConfig config, string inputFile, string originalFilePath, string? albumArtPath, CancellationToken cancellationToken)
        {
            var sourceExtension = Path.GetExtension(originalFilePath);

            if (!config.SourceExtensions.Contains(sourceExtension, StringComparer.OrdinalIgnoreCase))
            {
                throw new Exception("Cannot convert files not in SourceExtensions");
            }

            FfProbeResult mediaAnalysis;
            try
            {
                mediaAnalysis = await AnalyseAsync(inputFile, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception("Error during FFProbe: " + ex);
            }

            IReadOnlyList<KeyValuePair<string, string>> tags = await (_tagReaders.FirstOrDefault(x => x.CanHandle(sourceExtension)) ?? _ffmpegTagReader).GetTags(mediaAnalysis, inputFile, sourceExtension, cancellationToken);
            tags = FilterTags(tags);
            tags = SanitizeTags(tags, config.DeviceConfig.TagCharacterLimitations, config.DeviceConfig.TagValueDelimiter);

            if (mediaAnalysis.Streams.Count(x => x.CodecType == FfProbeCodecType.Audio) != 1)
            {
                throw new Exception("Files must have exactly one audio stream");
            }

            var audioStream = mediaAnalysis.Streams.Single(x => x.CodecType == FfProbeCodecType.Audio);

            var hasEmbeddedCover = mediaAnalysis.Streams.Any(x => x.CodecType == FfProbeCodecType.Video);
            var hasAlbumArt = hasEmbeddedCover || albumArtPath != null;

            var encoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension);
            var albumArtConfig = config.DeviceConfig.AlbumArt;

            var overrides = (config.PathFormatOverrides ?? Enumerable.Empty<KeyValuePair<string, FileFormatOverride>>()).Where(x => _pathMatcher.Matches(x.Key, originalFilePath, false)).Select(x => x.Value).ToList();
            var mergedOverrides = overrides.Any() ? MergeOverrides(overrides) : null;

            if (!IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, mediaAnalysis) || (mergedOverrides != null && !IsWithinLimitations(mergedOverrides, sourceExtension, mediaAnalysis)))
            {
                if (mergedOverrides != null)
                {
                    encoderInfo = GetEncoderInfoOverride(config.DeviceConfig.FallbackFormat, mergedOverrides);
                }
                else
                {
                    encoderInfo = config.DeviceConfig.FallbackFormat;
                }
            }

            if (encoderInfo.Codec == "libvorbis" && audioStream.SampleRateHz > 48000)
            {
                encoderInfo.SampleRateHz = 48000;
            }

            var tagWriter = _tagWriters.FirstOrDefault(x => x.CanHandle(encoderInfo.Extension));

            var ffmpegTags = tagWriter != null ? null : _ffmpegTagMapper.GetFfmpegFromVorbis(tags, encoderInfo.Extension);
            var (outputFile, albumArtToEmbed) = await Convert(inputFile, hasEmbeddedCover, albumArtPath, encoderInfo, albumArtConfig, ffmpegTags, cancellationToken);

            if (tagWriter != null)
            {
                await tagWriter.SetTags(tags, albumArtToEmbed != null ? new[] { albumArtToEmbed.Value } : Array.Empty<AlbumArt>(), outputFile, cancellationToken);
            }
            else if (albumArtToEmbed != null)
            {
                _logger.LogWarning("Couldn't embed album art in output file. This is likely due to FFMPEG not being able to write album art to ogg files. Install vorbis-tools to fix this.");
            }

            return (outputFile, encoderInfo.Extension);
        }

        private static async Task<FfProbeResult> AnalyseAsync(string inputFile, CancellationToken cancellationToken)
        {
            var args = new[]
            {
                "-loglevel", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                inputFile
            };

            using var stdout = new StringWriter();
            await ProcessStartHelper.RunProcess("ffprobe", args, stdout, cancellationToken: cancellationToken);
            return JsonSerializer.Deserialize<FfProbeResult>(stdout.ToString()) ?? throw new ArgumentException("ffprobe result was null");
        }

        private IReadOnlyList<KeyValuePair<string, string>> FilterTags(IReadOnlyList<KeyValuePair<string, string>> tags)
        {
            var toReturn = new List<KeyValuePair<string, string>>();
            foreach (var tag in tags)
            {
                switch (tag.Key.ToUpperInvariant())
                {
                    case "ENCODER":
                        break;
                    default:
                        toReturn.Add(tag);
                        break;
                }
            }
            return toReturn;
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
                if ((toReturn.Extension != null && toReturn.Extension != toApply.Extension) ||
                    (toReturn.Codec != null && toReturn.Codec != toApply.Codec) ||
                    (toReturn.Profile != null && toReturn.Profile != toApply.Profile) ||
                    (toReturn.Muxer != null && toReturn.Muxer != toApply.Muxer))
                {
                    toReturn.Extension = toApply.Extension;
                    toReturn.Codec = toApply.Codec;
                    toReturn.Profile = toApply.Profile;
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

        private static EncoderInfo GetEncoderInfoRemux(FfProbeResult mediaAnalysis, string sourceExtension)
        {
            // this is pretty dumb, but the muxer ffprobe spits out and the one that ffmpeg needs are different
            // also, ffprobe sometimes misdetects files, so we're just going by file ending here while we can
            return sourceExtension.ToLowerInvariant() switch
            {
                ".m4a" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "ipod",
                    AdditionalFlags = "-movflags faststart",
                    Extension = sourceExtension
                },
                ".aac" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "adts",
                    Extension = sourceExtension
                },
                ".wma" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "asf",
                    Extension = sourceExtension
                },
                ".ogg" or ".opus" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "ogg",
                    Extension = sourceExtension
                },
                ".mp3" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "mp3",
                    Extension = sourceExtension
                },
                ".flac" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "flac",
                    Extension = sourceExtension
                },
                ".wav" => new EncoderInfo
                {
                    Codec = "copy",
                    Muxer = "wav",
                    Extension = sourceExtension
                },
                _ => throw new ArgumentException($"don't know how to remux {sourceExtension} ({mediaAnalysis.Format.FormatName})"),
            };
        }

        private IReadOnlyList<KeyValuePair<string, string>> SanitizeTags(IReadOnlyList<KeyValuePair<string, string>> tags, CharacterLimitations? characterLimitations, string? tagValueDelimiter)
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
                var tagValue = _sanitizer.SanitizeText(characterLimitations, tag.Value, out var hasUnsupportedChars);
                if (hasUnsupportedChars)
                    _logger.LogInformation("Unsupported chars in tag {Tag}: {TagValue}", tag.Key, FormatLogValue(tag.Value));
                toReturn.Add(new KeyValuePair<string, string>(tag.Key, tagValue));
            }

            return toReturn;
        }

        private string FormatLogValue(string value)
        {
            if (value.Contains('\n'))
            {
                return $"{Environment.NewLine}\t{value.ReplaceLineEndings($"{Environment.NewLine}\t")}";
            }
            else
            {
                return value;
            }
        }

        private bool IsSupported(IList<FileFormatLimitation> supportedFormats, string sourceExtension, FfProbeResult mediaAnalysis)
        {
            foreach (var supportedFormat in supportedFormats)
            {
                if (IsWithinLimitations(supportedFormat, sourceExtension, mediaAnalysis))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsWithinLimitations(FileFormatLimitation limitation, string sourceExtension, FfProbeResult mediaAnalysis)
        {
            var audioStream = mediaAnalysis.Streams.Single(x => x.CodecType == FfProbeCodecType.Audio);

            return (limitation.Extension == null || limitation.Extension.Equals(sourceExtension, StringComparison.OrdinalIgnoreCase)) &&
                (limitation.Codec == null || limitation.Codec.Equals(audioStream.CodecName, StringComparison.OrdinalIgnoreCase)) &&
                (limitation.Profile == null || limitation.Profile.Equals(audioStream.Profile, StringComparison.OrdinalIgnoreCase)) &&
                (limitation.MaxChannels == null || limitation.MaxChannels >= audioStream.Channels) &&
                (limitation.MaxSampleRateHz == null || limitation.MaxSampleRateHz >= audioStream.SampleRateHz) &&
                (limitation.MaxBitrate == null || limitation.MaxBitrate >= (audioStream.BitRate ?? mediaAnalysis.Format.BitRate) / 1000);
        }

        private async Task<(string OutFile, AlbumArt? AlbumArtToEmbed)> Convert(string sourcePath, bool hasEmbeddedCover, string? externalCoverPath, EncoderInfo encoderInfo, AlbumArtConfig? albumArtConfig, IReadOnlyDictionary<string, string>? tags, CancellationToken cancellationToken)
        {
            var args = new List<string>();
            args.AddRange(new[] { "-i", sourcePath });

            var hasExternalCover = externalCoverPath != null;
            if (albumArtConfig?.Codec != null && hasExternalCover)
            {
                args.AddRange(new[] { "-i", externalCoverPath! });
            }

            args.AddRange(new[] { "-map", "0:a" });

            args.AddRange(new[] { "-map_metadata", "-1" });

            args.AddRange(new[] { "-c:a", encoderInfo.Codec });

            if (!string.IsNullOrEmpty(encoderInfo.Profile))
            {
                args.AddRange(new[] { "-profile:a", encoderInfo.Profile });
            }

            if (encoderInfo.Channels.HasValue)
            {
                args.AddRange(new[] { "-ac", encoderInfo.Channels.Value.ToString() });
            }

            if (encoderInfo.SampleRateHz.HasValue)
            {
                args.AddRange(new[] { "-ar", encoderInfo.SampleRateHz.Value.ToString() });
            }

            if (encoderInfo.Bitrate.HasValue)
            {
                args.AddRange(new[] { "-b:a", (encoderInfo.Bitrate.Value * 1000).ToString() });
            }

            if (encoderInfo.Muxer == "mp3")
            {
                args.AddRange(new[] { "-id3v2_version", "3" });
            }

            if (!string.IsNullOrEmpty(encoderInfo.AdditionalFlags))
            {
                args.AddRange(encoderInfo.AdditionalFlags.Split(' '));
            }

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    args.AddRange(new[] { "-metadata", $"{tag.Key}={tag.Value}" });
                }
            }

            string? albumArtOutput = null;
            if (albumArtConfig?.Codec != null && (hasEmbeddedCover || hasExternalCover))
            {
                string albumArtInput;
                if (hasEmbeddedCover)
                {
                    albumArtInput = "0:v";
                }
                else if (hasExternalCover)
                {
                    albumArtInput = "1:v";
                }
                else
                {
                    throw new InvalidOperationException();
                }

                if (albumArtConfig.ResizeType == ImageResizeType.None)
                {
                    albumArtOutput = albumArtInput;
                }
                else
                {
                    var width = albumArtConfig.Width ?? throw new ArgumentException("AlbumArt config must have width and height when a resize type is set");
                    var height = albumArtConfig.Height ?? throw new ArgumentException("AlbumArt config must have width and height when a resize type is set");

                    switch (albumArtConfig.ResizeType)
                    {
                        case ImageResizeType.KeepInputAspectRatio:
                            args.AddRange(new[] { "-filter_complex", $"[{albumArtInput}]scale='min({width},iw)':min'({height},ih)':force_original_aspect_ratio=decrease[albumart]" });
                            break;
                        case ImageResizeType.ForceOutputAspectRatio:
                            args.AddRange(new[] { "-filter_complex", $"[{albumArtInput}]scale='min({width},iw)':'min({height},ih)':force_original_aspect_ratio=decrease,split [original][copy]; " + // fit the image into the target image size; split
                                $"[copy]scale='if(gt(a,{width}/{height}),iw,ih*({width}/{height}))':'if(gt(a,{width}/{height}),iw/({width}/{height}),ih)',gblur=sigma=10[blurred]; " + // stretch the image to the target aspect ratio without scaling it up; blur
                                $"[blurred][original]overlay=(main_w-overlay_w)/2:(main_h-overlay_h)/2[albumart]" }); // overlay the scaled image centered over the blurred image
                            break;
                        case ImageResizeType.ForceOutputSize:
                            args.AddRange(new[] { "-filter_complex", $"[{albumArtInput}]split [original][copy]; " +
                                $"[copy]scale={width}:{height},gblur=sigma=10[blurred]; " + // stretch the image to the target size; blur
                                $"[original]scale={width}:{height}:force_original_aspect_ratio=decrease[scaled]; " + // fit the image into the target image size
                                $"[blurred][scaled]overlay=(main_w-overlay_w)/2:(main_h-overlay_h)/2[albumart]" }); // overlay the scaled image centered over the blurred image
                            break;
                        default:
                            throw new IndexOutOfRangeException($"Invalid CropType {albumArtConfig.ResizeType}");
                    }
                    albumArtOutput = "[albumart]";
                }
            }
            else
            {
                args.AddRange(new[] { "-vn" });
            }

            var ffmpegCanEmbed = encoderInfo.Muxer != "ogg"; // https://trac.ffmpeg.org/ticket/4448

            var outFilePath = _tempFileSession.GetTempFilePath();
            string? albumArtPath = null;
            string? albumArtMimeType = null;
            if (albumArtOutput == null)
            {
                args.AddRange(new[] { "-f", encoderInfo.Muxer });
                args.Add(outFilePath);
            }
            else if (ffmpegCanEmbed)
            {
                args.AddRange(new[] { "-map", albumArtOutput });
                args.AddRange(new[] { "-disposition:v", "attached_pic", "-metadata:s:v", "comment=Cover (front)" });
                args.AddRange(new[] { "-c:v", albumArtConfig!.Codec! });
                args.AddRange(new[] { "-f", encoderInfo.Muxer });
                args.Add(outFilePath);
            }
            else
            {
                args.AddRange(new[] { "-f", encoderInfo.Muxer });
                args.Add(outFilePath);

                args.AddRange(new[] { "-map", albumArtOutput });


                (var muxer, albumArtMimeType) = albumArtConfig!.Codec! switch
                {
                    "mjpeg" => ("image2", "image/jpeg"),
                    "png" => ("image2", "image/png"),
                    _ => throw new NotSupportedException($"Unsupported album art codec {albumArtConfig!.Codec!}"),
                };

                args.AddRange(new[] { "-c:v", albumArtConfig!.Codec! });
                args.AddRange(new[] { "-f", muxer });

                albumArtPath = _tempFileSession.GetTempFilePath();
                args.Add(albumArtPath);
            }

            await ProcessStartHelper.RunProcess("ffmpeg", args, null, null, process => process.StandardInput.Write('q'), cancellationToken: cancellationToken);

            if (albumArtPath != null)
            {
                return (outFilePath, new AlbumArt(ApicType.CoverFront, albumArtMimeType!, null, await File.ReadAllBytesAsync(albumArtPath, cancellationToken)));
            }
            else
            {
                return (outFilePath, null);
            }
        }
    }
}
