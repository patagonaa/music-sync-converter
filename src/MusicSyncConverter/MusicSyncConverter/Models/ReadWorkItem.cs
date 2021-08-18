namespace MusicSyncConverter.Models
{
    public class ReadWorkItem
    {
        public CompareResultType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; }
        public string ExistingTargetFile { get; set; }
    }
}
