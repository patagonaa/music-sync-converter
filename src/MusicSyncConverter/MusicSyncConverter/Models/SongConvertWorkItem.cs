namespace MusicSyncConverter.Models
{
    public class SongConvertWorkItem
    {
        public ConvertActionType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; } = null!;
        public string SourceTempFilePath { get; set; } = null!;
        public string? AlbumArtPath { get; set; }
        public string TargetFilePath { get; set; } = null!;
    }
}
