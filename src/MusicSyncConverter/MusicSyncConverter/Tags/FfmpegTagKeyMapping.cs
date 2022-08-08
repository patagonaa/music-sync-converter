using System;
using System.Collections.Generic;

namespace MusicSyncConverter.Tags
{
    internal class FfmpegTagKeyMapping
    {
        // https://wiki.hydrogenaud.io/index.php?title=Tag_Mapping
        // https://wiki.multimedia.cx/index.php/FFmpeg_Metadata

        private static readonly IDictionary<string, string> _ffmpegToVorbis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "album", "ALBUM" },
            { "album_artist", "ALBUMARTIST" },
            { "performer", "PERFORMER" },
            { "conductor", "CONDUCTOR" },
            { "artist", "ARTIST" },
            { "description", "DESCRIPTION" },
            { "comment", "COMMENT" },
            { "lyrics", "UNSYNCEDLYRICS" },
            { "composer", "COMPOSER" },
            { "date", "DATE" },
            { "year", "DATE" },
            { "TDAT", "DATE" },
            { "disc", "DISCNUMBER" },
            { "genre", "GENRE" },
            { "title", "TITLE" },
            { "track", "TRACKNUMBER" },
            { "publisher", "PUBLISHER" },
            { "language", "LANGUAGE" },
            { "website", "WEBSITE" },
            { "purl", "WEBSITE" },
            { "copyright", "COPYRIGHT" },
            { "TSRC", "ISRC" },
            { "ISRC", "ISRC" },
            { "author", "AUTHOR" },
            { "compilation", "COMPILATION" },
            { "MIXARTIST", "REMIXER" },
            { "TPE4", "REMIXER" },
            { "REMIXER", "REMIXER" },
            { "TBPM", "BPM" },
            { "TOPE", "BPM" },
        };
        private static readonly IDictionary<string, string> _vorbisToFfmpeg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ALBUM", "album" },
            { "ALBUMARTIST", "album_artist" },
            { "CONDUCTOR", "performer" },
            { "PERFORMER", "performer" },
            { "ARTIST", "artist" },
            { "DESCRIPTION", "comment" },
            { "COMMENT", "comment" },
            { "UNSYNCEDLYRICS", "lyrics" },
            { "COMPOSER", "composer" },
            { "DATE", "date" },
            { "DISCNUMBER", "disc" },
            { "GENRE", "genre" },
            { "TITLE", "title" },
            { "TRACKNUMBER", "track" },
            { "PUBLISHER", "publisher" },
            { "ORGANIZATION", "publisher" },
            { "LANGUAGE", "language" },
            { "WEBSITE", "website" },
            { "WWW", "website" },
            { "COPYRIGHT", "copyright" },
            { "ISRC", "isrc" },
            { "AUTHOR", "author" },
            { "COMPILATION", "compilation" },
        };

        public static string? GetVorbisKey(string ffmpegKey)
        {
            return _ffmpegToVorbis.TryGetValue(ffmpegKey, out var vorbisKey) ? vorbisKey : null;
        }

        public static string? GetFfmpegKey(string vorbisKey)
        {
            return _vorbisToFfmpeg.TryGetValue(vorbisKey, out var ffmpegKey) ? ffmpegKey : null;
        }
    }
}
