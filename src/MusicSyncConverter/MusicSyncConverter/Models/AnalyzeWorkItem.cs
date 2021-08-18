namespace MusicSyncConverter.Models
{

    public class AnalyzeWorkItem
    {
        public AnalyzeActionType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; }
        public byte[] SourceFileContents { get; set; }
        public string ExistingTargetFile { get; set; }
    }
}
