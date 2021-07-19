namespace MusicSyncConverter.Models
{
    public class WorkItem
    {
        public ActionType ActionType { get; set; }
        public SourceFile SourceFileInfo { get; set; }
        public TargetFileInfo TargetFileInfo { get; set; }
    }
}
