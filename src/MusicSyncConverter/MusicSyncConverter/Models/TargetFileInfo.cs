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
    }
}