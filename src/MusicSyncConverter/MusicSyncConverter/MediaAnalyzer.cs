using FFMpegCore;
using MusicSyncConverter.Config;
using MusicSyncConverter.Models;
using System;
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

        public async Task<ConvertWorkItem> Analyze(SyncConfig config, AnalyzeWorkItem workItem)
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

        private async Task<ConvertWorkItem> GetWorkItemCopyOrConvert(SyncConfig config, AnalyzeWorkItem workItem)
        {
            var sourceExtension = Path.GetExtension(workItem.SourceFileInfo.RelativePath);

            if (!config.SourceExtensions.Contains(sourceExtension, StringComparer.OrdinalIgnoreCase))
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

            var targetFilePath = Path.Combine(config.TargetDir, _sanitizer.SanitizeText(config.DeviceConfig.CharacterLimitations, workItem.SourceFileInfo.RelativePath, true));

            if (IsSupported(config.DeviceConfig.SupportedFormats, sourceExtension, audioStream.CodecName, audioStream.Profile))
            {
                var copyAlbumArt = !string.IsNullOrEmpty(config.DeviceConfig.FallbackFormat.CoverCodec); // only copy album art if we want it
                return new ConvertWorkItem
                {
                    ActionType = ConvertActionType.Remux,
                    SourceFileInfo = workItem.SourceFileInfo,
                    SourceTempFilePath = workItem.SourceTempFilePath,
                    TargetFilePath = targetFilePath,
                    EncoderInfo = GetEncoderInfoRemux(mediaAnalysis, sourceExtension, copyAlbumArt),
                    Tags = tags
                };
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

        private EncoderInfo GetEncoderInfoRemux(IMediaAnalysis mediaAnalysis, string sourceExtension, bool copyAlbumArt)
        {
            var coverCodec = copyAlbumArt ? "copy" : null;
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
                        CoverCodec = coverCodec,
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
                        CoverCodec = coverCodec,
                        Extension = sourceExtension
                    };
                case ".ogg":
                case ".opus":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "ogg",
                        CoverCodec = coverCodec,
                        Extension = sourceExtension
                    };
                case ".mp3":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "mp3",
                        CoverCodec = coverCodec,
                        Extension = sourceExtension
                    };
                case ".flac":
                    return new EncoderInfo
                    {
                        Codec = "copy",
                        Muxer = "flac",
                        CoverCodec = coverCodec,
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

        private Dictionary<string, string> MapTags(IMediaAnalysis mediaAnalysis, CharacterLimitations characterLimitations)
        {
            var toReturn = new Dictionary<string, string>();
            foreach (var tag in mediaAnalysis.Format.Tags ?? new Dictionary<string, string>())
            {
                if (!_supportedTags.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                toReturn[tag.Key] = _sanitizer.SanitizeText(characterLimitations, tag.Value, false);
            }
            foreach (var tag in mediaAnalysis.PrimaryAudioStream.Tags ?? new Dictionary<string, string>())
            {
                if (!_supportedTags.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                toReturn[tag.Key] = _sanitizer.SanitizeText(characterLimitations, tag.Value, false);
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
    }
}
