namespace MusicSyncConverter.Models
{

    public class AnalyzeWorkItem
    {
        public AnalyzeActionType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; }
        public string SourceTempFilePath { get; set; }
        public string AlbumArtPath { get; set; }
        public string ExistingTargetFile { get; set; }
    }
}
