using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class FfmpegTagKeyMapping
    {
        private static readonly IList<KeyValuePair<string, string>> _ffmpegToVorbis = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("album", "ALBUM"),
            new KeyValuePair<string, string>("album_artist", "ALBUMARTIST"),
            new KeyValuePair<string, string>("artist", "ARTIST"),
            new KeyValuePair<string, string>("comment", "DESCRIPTION"),
            new KeyValuePair<string, string>("composer", "COMPOSER"),
            new KeyValuePair<string, string>("date", "DATE"),
            new KeyValuePair<string, string>("disc", "DISCNUMBER"),
            new KeyValuePair<string, string>("genre", "GENRE"),
            new KeyValuePair<string, string>("title", "TITLE"),
            new KeyValuePair<string, string>("track", "TRACKNUMBER")
        };

        public static string? GetVorbisKey(string ffmpegKey)
        {
            return _ffmpegToVorbis.FirstOrDefault(x => x.Key.Equals(ffmpegKey, StringComparison.OrdinalIgnoreCase)).Value;
        }

        public static string? GetFfmpegKey(string vorbisKey)
        {
            return _ffmpegToVorbis.FirstOrDefault(x => x.Value.Equals(vorbisKey, StringComparison.OrdinalIgnoreCase)).Value;
        }
    }
}
