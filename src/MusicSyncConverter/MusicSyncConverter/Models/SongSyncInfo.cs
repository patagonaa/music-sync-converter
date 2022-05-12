namespace MusicSyncConverter.Models
{
    public class SongSyncInfo
    {
        public SourceFileInfo SourceFileInfo { get; set; } = null!;
        public string TargetPath { get; set; } = null!; // Audio/Music/Test.mp3
    }
}