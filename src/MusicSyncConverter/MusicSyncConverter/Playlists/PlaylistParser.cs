using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MusicSyncConverter.Playlists
{
    internal class PlaylistParser
    {
        public async Task<IReadOnlyList<PlaylistSong>> ParseM3u(StreamReader sr)
        {
            var playlistSongs = new List<PlaylistSong>();
            string line;
            var metaData = new Dictionary<string, string>();
            while ((line = (await sr.ReadLineAsync())!) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line == "#EXTM3U")
                    continue;
                if (line.StartsWith('#'))
                {
                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex > 0)
                    {
                        var key = line[1..separatorIndex];
                        var value = line[(separatorIndex + 1)..];
                        metaData[key] = value;
                    }
                }
                else
                {
                    var song = new PlaylistSong(line);
                    if (metaData.TryGetValue("EXTINF", out var extinf))
                    {
                        song.Name = extinf.Split(',', 2)[1].Trim();
                    }
                    metaData.Clear();

                    playlistSongs.Add(song);
                }
            }
            return playlistSongs;
        }
    }
}