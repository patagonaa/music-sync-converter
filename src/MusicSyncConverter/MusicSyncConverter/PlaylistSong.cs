namespace MusicSyncConverter
{
    public class PlaylistSong
    {
        public PlaylistSong(string path)
        {
            Path = path;
        }
        public string Path { get; }

        public string? Name { get; set; }
    }
}