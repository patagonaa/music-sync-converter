namespace MusicSyncConverter
{
    class WorkItem
    {
        public ActionType ActionType { get; set; }
        public SourceFile SourceFile { get; set; }
        public string TargetFilePath { get; set; } // E:/Audio/Music/Test.mp3
    }
}
