﻿using System.Collections.Generic;

namespace MusicSyncConverter.Models
{
    public class ConvertWorkItem
    {
        public ConvertActionType ActionType { get; set; }
        public SourceFileInfo SourceFileInfo { get; set; }
        public byte[] SourceFileContents { get; set; }
        public string TargetFilePath { get; set; }
        public EncoderInfo EncoderInfo { get; set; }
        public Dictionary<string, string> Tags { get; set; }
    }
}
