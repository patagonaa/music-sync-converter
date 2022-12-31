using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MusicSyncConverter.Playlists
{
    public class PlaylistWriter
    {
        public async Task WriteM3u(StreamWriter sw, IList<PlaylistSong> songs)
        {
            await sw.WriteLineAsync("#EXTM3U");
            foreach (var song in songs)
            {
                await sw.WriteLineAsync($"#EXTINF:0,{song.Name ?? throw new ArgumentException("Song must have Name", nameof(songs))}");
                await sw.WriteLineAsync(song.Path);
            }
        }
    }
}
