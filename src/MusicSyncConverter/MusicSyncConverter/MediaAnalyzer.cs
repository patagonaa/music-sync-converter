using FFMpegCore;
using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class MediaAnalyzer
    {
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

        public MediaAnalyzer(TextSanitizer sanitizer)
        {
            _sanitizer = sanitizer;
        }

        public async Task<ConvertWorkItem> Analyze(SyncConfig config, AnalyzeWorkItem workItem, IProducerConsumerCollection<string> infoLogMessages)
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
                    Console.WriteLine($"--> Analyze {workItem.SourceFileInfo.RelativePath}");
                    toReturn = await GetWorkItemCopyOrConvert(config, workItem, infoLogMessages);
                    Console.WriteLine($"<-- Analyze {workItem.SourceFileInfo.RelativePath}");
                    break;
                default:
                    throw new ArgumentException("Invalid AnalyzeActionType");
            }
            return toReturn;
        }

        private async Task<ConvertWorkItem> GetWorkItemCopyOrConvert(SyncConfig config, AnalyzeWorkItem workItem, IProducerConsumerCollection<string> infoLogMessages)
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

            var targetFilePath = _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, workItem.SourceFileInfo.RelativePath, true, out var hasUnsupportedChars);
            if (hasUnsupportedChars)
                infoLogMessages.TryAdd($"Unsupported chars in path: {workItem.SourceFileInfo.RelativePath}");

            if (IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream))
            {
                return new ConvertWorkItem
                {
                    ActionType = ConvertActionType.Remux,
                    SourceFileInfo = workItem.SourceFileInfo,
                    SourceTempFilePath = workItem.SourceTempFilePath,
                    TargetFilePath = targetFilePath,
                    EncoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension, config.DeviceConfig.FallbackFormat),
                    Tags = tags,
                    AlbumArtPath = workItem.AlbumArtPath
                };
            }

            return new ConvertWorkItem
            {
                ActionType = ConvertActionType.Transcode,
                SourceFileInfo = workItem.SourceFileInfo,
                SourceTempFilePath = workItem.SourceTempFilePath,
                TargetFilePath = Path.ChangeExtension(targetFilePath, config.DeviceConfig.FallbackFormat.Extension),
                EncoderInfo = config.DeviceConfig.FallbackFormat,
                Tags = tags,
                AlbumArtPath = workItem.AlbumArtPath
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
                if(hasUnsupportedChars)
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
    }
}
