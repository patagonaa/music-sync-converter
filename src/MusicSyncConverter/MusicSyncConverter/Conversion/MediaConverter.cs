using FFMpegCore;
using Microsoft.Extensions.Logging;
using MusicSyncConverter.Config;
using MusicSyncConverter.Tags;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            IMediaAnalysis mediaAnalysis;
            try
            {
                mediaAnalysis = await FFProbe.AnalyseAsync(inputFile);
            }
            catch (Exception ex)
            {
                throw new Exception("Error during FFProbe: " + ex);
            }

            IReadOnlyList<KeyValuePair<string, string>> tags = await (_tagReaders.FirstOrDefault(x => x.CanHandle(inputFile, sourceExtension)) ?? _ffmpegTagReader).GetTags(mediaAnalysis, inputFile, sourceExtension, cancellationToken);
            tags = FilterTags(tags);
            tags = SanitizeTags(tags, originalFilePath, config.DeviceConfig.TagCharacterLimitations, config.DeviceConfig.TagValueDelimiter);

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

            var tagWriter = _tagWriters.FirstOrDefault(x => x.CanHandle(outputFile, encoderInfo.Extension));

            var ffmpegTags = tagWriter != null ? null : _ffmpegTagMapper.GetFfmpegFromVorbis(tags, encoderInfo.Extension);
            await Convert(inputFile, mediaAnalysis.PrimaryVideoStream != null, albumArtPath, outputFile, encoderInfo, ffmpegTags, cancellationToken);

            if (tagWriter != null)
                await tagWriter.SetTags(tags, outputFile, cancellationToken);

            return (outputFile, encoderInfo.Extension);
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

        private IReadOnlyList<KeyValuePair<string, string>> SanitizeTags(IReadOnlyList<KeyValuePair<string, string>> tags, string originalPath, CharacterLimitations? characterLimitations, string? tagValueDelimiter)
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
                    _logger.LogInformation("Unsupported chars in tag {Tag}: {TagValue}", tag.Key, FormatLogValue(tag.Value));
                toReturn.Add(new KeyValuePair<string, string>(tag.Key, tagValue));
            }

            return toReturn;
        }

        private string FormatLogValue(string value)
        {
            if (value.Contains("\n"))
            {
                return $"{Environment.NewLine}\t{value.ReplaceLineEndings($"{Environment.NewLine}\t")}";
            }
            else
            {
                return value;
            }
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

        private async Task<string> Convert(string sourcePath, bool hasEmbeddedCover, string? externalCoverPath, string outFilePath, EncoderInfo encoderInfo, IReadOnlyDictionary<string, string>? tags, CancellationToken cancellationToken)
        {
            var args = new List<string>();
            args.AddRange(new[] { "-i", sourcePath });

            var coverSupported = !string.IsNullOrEmpty(encoderInfo.CoverCodec);

            var hasExternalCover = externalCoverPath != null;
            if (coverSupported && hasExternalCover)
            {
                args.AddRange(new[] { "-i", externalCoverPath! });
            }

            args.AddRange(new[] { "-map", "0:a" });

            if (coverSupported)
            {
                if (hasEmbeddedCover)
                {
                    args.AddRange(new[] { "-map", "0:v", "-disposition:v", "attached_pic" });
                }
                else if (hasExternalCover)
                {
                    args.AddRange(new[] { "-map", "1:v", "-disposition:v", "attached_pic" });
                }
            }

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

            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    args.AddRange(new[] { "-metadata", $"{tag.Key}={tag.Value}" });
                }
            }

            if (!string.IsNullOrEmpty(encoderInfo.CoverCodec))
            {
                args.AddRange(new[] { "-c:v", encoderInfo.CoverCodec });
                if (encoderInfo.MaxCoverSize != null)
                {
                    args.AddRange(new[] { "-vf", $"scale='min({encoderInfo.MaxCoverSize.Value},iw)':min'({encoderInfo.MaxCoverSize.Value},ih)':force_original_aspect_ratio=decrease" });
                }
            }
            else
            {
                args.AddRange(new[] { "-vn" });
            }

            if (!string.IsNullOrEmpty(encoderInfo.AdditionalFlags))
            {
                args.AddRange(encoderInfo.AdditionalFlags.Split(' '));
            }
            args.AddRange(new[] { "-f", encoderInfo.Muxer });
            args.Add(outFilePath);


            var startInfo = new ProcessStartInfo("ffmpeg") { UseShellExecute = false };
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardOutput = true;
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (o, e) => Debug.WriteLine(e.Data);
            process.ErrorDataReceived += (o, e) => Debug.WriteLine(e.Data);

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return outFilePath;
        }
    }
}
