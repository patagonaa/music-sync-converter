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
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class MediaConverter
    {
        public MediaConverter()
        {
            _sanitizer = new TextSanitizer();
        }

        private readonly TextSanitizer _sanitizer;
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

        public async Task<OutputFile> RemuxOrConvert(SyncConfig config, ConvertWorkItem workItem, IProducerConsumerCollection<string> infoLogMessages, CancellationToken cancellationToken)
        {
            var sourceExtension = Path.GetExtension(workItem.SourceFileInfo.RelativePath);

            if (!config.SourceExtensions.Contains(sourceExtension, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            IMediaAnalysis mediaAnalysis;
            try
            {
                mediaAnalysis = await FFProbe.AnalyseAsync(workItem.SourceTempFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while FFProbe {workItem.SourceFileInfo.RelativePath}: {ex}");
                return null;
            }

            var tags = MapTags(mediaAnalysis, workItem.SourceFileInfo.RelativePath, config.DeviceConfig.CharacterLimitations, infoLogMessages);

            var audioStream = mediaAnalysis.PrimaryAudioStream;

            if (audioStream == null)
            {
                Console.WriteLine($"Missing Audio stream: {workItem.SourceFileInfo.RelativePath}");
                return null;
            }

            EncoderInfo encoderInfo;

            if (IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream))
            {
                encoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension, config.DeviceConfig.FallbackFormat);
            }
            else
            {
                encoderInfo = config.DeviceConfig.FallbackFormat;
            }

            var file = await Convert(workItem.SourceTempFilePath, workItem.AlbumArtPath, encoderInfo, tags, cancellationToken);
            return new OutputFile
            {
                ModifiedDate = workItem.SourceFileInfo.ModifiedDate,
                TempFilePath = file,
                Path = Path.ChangeExtension(workItem.TargetFilePath, encoderInfo.Extension)
            };
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

        private Dictionary<string, string> MapTags(IMediaAnalysis mediaAnalysis, string relativePath, CharacterLimitations characterLimitations, IProducerConsumerCollection<string> infoLogMessages)
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
            foreach (var tag in mediaAnalysis.PrimaryAudioStream.Tags ?? new Dictionary<string, string>())
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

        private bool IsSupported(IList<DeviceFileFormat> supportedFormats, string sourceExtension, AudioStream audioStream)
        {
            foreach (var supportedFormat in supportedFormats)
            {
                if (supportedFormat.Extension.Equals(sourceExtension, StringComparison.OrdinalIgnoreCase) &&
                    (supportedFormat.Codec == null || supportedFormat.Codec.Equals(audioStream.CodecName, StringComparison.OrdinalIgnoreCase)) &&
                    (supportedFormat.Profile == null || supportedFormat.Profile.Equals(audioStream.Profile, StringComparison.OrdinalIgnoreCase)) &&
                    (supportedFormat.MaxChannels == null || supportedFormat.MaxChannels >= audioStream.Channels) &&
                    (supportedFormat.MaxSampleRateHz == null || supportedFormat.MaxSampleRateHz >= audioStream.SampleRateHz))
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<string> Convert(string sourcePath, string coverPath, EncoderInfo encoderInfo, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            var outFilePath = TempFileHelper.GetTempFilePath();
            var args = FFMpegArguments
                .FromFileInput(sourcePath);
            var hasExternalCover = encoderInfo.CoverCodec != null && coverPath != null;
            if (hasExternalCover)
            {
                args.AddFileInput(coverPath);
            }

            var argsProcessor = args.OutputToFile(outFilePath, true, x => // we do not use pipe output here because ffmpeg can't write the header correctly when we use streams
            {
                x.WithArgument(new CustomArgument("-map 0:a"));
                if (hasExternalCover)
                {
                    x.WithArgument(new CustomArgument("-map 1:v -disposition:v attached_pic"));
                }
                else
                {
                    x.WithArgument(new CustomArgument("-map 0:v? -disposition:v attached_pic"));
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
