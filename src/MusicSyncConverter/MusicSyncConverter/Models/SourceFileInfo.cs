using Microsoft.Extensions.FileProviders;
using System;

namespace MusicSyncConverter.Models
{
    public class SourceFileInfo
    {
        public string RelativePath { get; set; } = null!; // Audio/Music/Test.mp3
        public DateTimeOffset ModifiedDate { get; set; }
    }
}