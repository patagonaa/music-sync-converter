using MusicSyncConverter.Models;
using System.Collections.Generic;

namespace MusicSyncConverter.Models
{
    public class TargetFileInfo
    {
        public TargetFileInfo(string absolutePath)
        {
            AbsolutePath = absolutePath;
        }
        public string AbsolutePath { get; } // E:/Audio/Music/Test.mp3
        public EncoderInfo EncoderInfo { get; set; }
        public Dictionary<string, string> Tags { get; set; }
    }
}