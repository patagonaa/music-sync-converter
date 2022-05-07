namespace MusicSyncConverter.Models
{
    public class ConvertWorkItem
    {
        public ConvertActionType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; }
        public string SourceTempFilePath { get; set; }
        public string AlbumArtPath { get; set; }
        public string TargetFilePath { get; set; }
    }
}
