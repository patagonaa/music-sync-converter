using Microsoft.Extensions.FileProviders;
using System;

namespace MusicSyncConverter.Models
{
    public class SourceFileInfo
    {
        public string Path { get; set; } = null!; // Audio/Music/Test.mp3
        public DateTimeOffset ModifiedDate { get; set; }
        public override string ToString()
        {
            return Path;
        }
    }
}