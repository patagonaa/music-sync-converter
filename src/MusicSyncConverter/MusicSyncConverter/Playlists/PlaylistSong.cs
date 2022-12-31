namespace MusicSyncConverter.Playlists
{
    public class PlaylistSong
    {
        public PlaylistSong(string path, string? name = null)
        {
            Path = path;
            Name = name;
        }
        public string Path { get; }

        public string? Name { get; set; }
    }
}