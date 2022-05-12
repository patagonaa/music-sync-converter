using MusicSyncConverter.Models;
using System.Collections.Generic;

namespace MusicSyncConverter
{
    internal class Playlist
    {
        public Playlist(SourceFileInfo playlistFileInfo, IReadOnlyList<PlaylistSong> songs)
        {
            PlaylistFileInfo = playlistFileInfo;
            Songs = songs;
        }
        public SourceFileInfo PlaylistFileInfo { get; }
        public IReadOnlyList<PlaylistSong> Songs { get; }
    }
}