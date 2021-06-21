namespace MusicSyncConverter
{
    class WorkItem
    {
        public ActionType ActionType { get; set; }
        public SourceFile SourceFile { get; set; }
        public string ExistingTargetFile { get; set; }
    }
}
