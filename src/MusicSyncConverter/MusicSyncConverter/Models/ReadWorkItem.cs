﻿namespace MusicSyncConverter.Models
{
    public class ReadWorkItem
    {
        public CompareResultType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; } = null!;
        public string TargetFilePath { get; set; } = null!;
    }
}
