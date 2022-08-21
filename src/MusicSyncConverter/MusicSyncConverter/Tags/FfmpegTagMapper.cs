using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicSyncConverter.Tags
{
    internal class FfmpegTagMapper
    {
        // https://wiki.hydrogenaud.io/index.php?title=Tag_Mapping
        // https://wiki.multimedia.cx/index.php/FFmpeg_Metadata
        // https://picard-docs.musicbrainz.org/en/appendices/tag_mapping.html

        private static readonly string[] _toSkip = new[] { "encoded_by", "major_brand", "minor_version", "compatible_brands", "handler_name", "vendor_id", "encoder", "creation_time" };
        private static readonly string[] _vorbisCommentExtensions = new[] { ".flac", ".ogg", ".opus" };
        private readonly ILogger _logger;

        public FfmpegTagMapper(ILogger logger)
        {
            _logger = logger;
        }

        public IReadOnlyDictionary<string, string> GetFfmpegFromVorbis(IEnumerable<KeyValuePair<string, string>> tags, string targetExtension)
        {
            var toReturn = new List<KeyValuePair<string, string>>();
            foreach (var tag in tags)
            {
                if (_vorbisCommentExtensions.Contains(targetExtension, StringComparer.OrdinalIgnoreCase))
                {
                    toReturn.Add(new KeyValuePair<string, string>(tag.Key, tag.Value));
                    continue;
                }

                switch (tag.Key.ToUpperInvariant())
                {
                    case "TRACKNUMBER":
                        var totalTracks = tags.FirstOrDefault(x => x.Key.Equals("TOTALTRACKS", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("TRACKTOTAL", StringComparison.OrdinalIgnoreCase));
                        toReturn.Add(new KeyValuePair<string, string>("track", totalTracks.Value != null ? $"{tag.Value}/{totalTracks.Value}" : tag.Value));
                        break;
                    case "TOTALTRACKS":
                    case "TRACKTOTAL":
                        continue;
                    case "DISCNUMBER":
                        var totalDiscs = tags.FirstOrDefault(x => x.Key.Equals("TOTALDISCS", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("DISCTOTAL", StringComparison.OrdinalIgnoreCase));
                        toReturn.Add(new KeyValuePair<string, string>("disc", totalDiscs.Value != null ? $"{tag.Value}/{totalDiscs.Value}" : tag.Value));
                        break;
                    case "TOTALDISCS":
                    case "DISCTOTAL":
                        continue;

                    case "COMPOSER":
                    case "GENRE":
                    case "ARTIST":
                    case "PERFORMER":
                    case "PUBLISHER":
                    case "ALBUM":
                    case "COPYRIGHT":
                    case "TITLE":
                    case "LANGUAGE":
                    case "COMPILATION":
                    case "DATE":
                    case "COMMENT":
                    case "DESCRIPTION":
                        toReturn.Add(new KeyValuePair<string, string>(tag.Key, tag.Value));
                        break;

                    case "ALBUMARTIST":
                        toReturn.Add(new KeyValuePair<string, string>("ALBUM_ARTIST", tag.Value));
                        break;
                    case "UNSYNCEDLYRICS":
                        toReturn.Add(new KeyValuePair<string, string>("LYRICS", tag.Value));
                        break;
                    case "ORGANIZATION":
                        toReturn.Add(new KeyValuePair<string, string>("PUBLISHER", tag.Value));
                        break;

                    // https://github.com/FFmpeg/FFmpeg/blob/e71d5156c8fec67a7198a0032262036ae7d46bcd/libavformat/id3v2.c
                    // ffmpeg should do this, but it doesn't, so we have to do it ourselves
                    case "REMIXER" when targetExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        toReturn.Add(new KeyValuePair<string, string>("TPE4", tag.Value));
                        break;
                    case "ISRC" when targetExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        toReturn.Add(new KeyValuePair<string, string>("TSRC", tag.Value));
                        break;
                    case "BPM" when targetExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        toReturn.Add(new KeyValuePair<string, string>("TBPM", tag.Value));
                        break;
                    case "ORIGARTIST" when targetExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        toReturn.Add(new KeyValuePair<string, string>("TOPE", tag.Value));
                        break;

                    default:
                        _logger.LogInformation("Could not map Vorbis key {VorbisKey} to FFMPEG {TargetExtension} key", tag.Key, targetExtension);
                        break;
                }
            }

            return toReturn.GroupBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => string.Join(';', group.Select(tag => tag.Value)));
        }

        public IEnumerable<KeyValuePair<string, string>> GetVorbisFromFfmpeg(IEnumerable<KeyValuePair<string, string>> tags, string sourceExtension)
        {
            foreach (var tag in tags)
            {
                var keyToUpper = tag.Key.ToUpperInvariant();
                switch (keyToUpper)
                {
                    case "COMPOSER":
                    case "GENRE":
                    case "ARTIST":
                    case "PERFORMER":
                    case "PUBLISHER":
                        foreach (var item in SplitTag(keyToUpper, tag.Value))
                        {
                            yield return item;
                        }
                        break;

                    case "ALBUM":
                    case "COPYRIGHT":
                    case "TITLE":
                    case "LANGUAGE":
                    case "COMPILATION":
                    case "DATE":
                    case "COMMENT":
                    case "DESCRIPTION":
                        yield return new KeyValuePair<string, string>(keyToUpper, tag.Value);
                        break;

                    case "ALBUM_ARTIST":
                        yield return new KeyValuePair<string, string>("ALBUMARTIST", tag.Value);
                        break;
                    case "DISC":
                        yield return new KeyValuePair<string, string>("DISCNUMBER", tag.Value);
                        break;
                    case "TRACK":
                        yield return new KeyValuePair<string, string>("TRACKNUMBER", tag.Value);
                        break;
                    case "LYRICS":
                        yield return new KeyValuePair<string, string>("UNSYNCEDLYRICS", tag.Value);
                        break;

                    case "TPE4" when sourceExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        yield return new KeyValuePair<string, string>("REMIXER", tag.Value);
                        break;
                    case "TSRC" when sourceExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        yield return new KeyValuePair<string, string>("ISRC", tag.Value);
                        break;
                    case "TBPM" when sourceExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        yield return new KeyValuePair<string, string>("BPM", tag.Value);
                        break;
                    case "TOPE" when sourceExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase):
                        yield return new KeyValuePair<string, string>("ORIGARTIST", tag.Value);
                        break;

                    case "REMIXER" when sourceExtension.Equals(".m4a", StringComparison.OrdinalIgnoreCase):
                        yield return new KeyValuePair<string, string>("REMIXER", tag.Value);
                        break;

                    default:
                        if (_toSkip.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                            break;
                        if (tag.Key.StartsWith("id3v2_priv.", StringComparison.OrdinalIgnoreCase))
                            break;
                        if (_vorbisCommentExtensions.Contains(sourceExtension, StringComparer.OrdinalIgnoreCase))
                        {
                            yield return new KeyValuePair<string, string>(tag.Key, tag.Value);
                            break;
                        }
                        _logger.LogInformation("Could not map FFMPEG {SourceExtension} key {FfmpegKey} to Vorbis key", sourceExtension, tag.Key);
                        break;
                }
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> SplitTag(string key, string rawValue)
        {
            var splitValue = rawValue.Split(';');
            foreach (var value in splitValue)
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }
}
