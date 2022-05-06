using Microsoft.Extensions.FileProviders;
using System;

namespace MusicSyncConverter.Models
{
    public class SourceFileInfo
    {
        public IFileProvider FileProvider { get; set; }
        public string RelativePath { get; internal set; } // Audio/Music/Test.mp3
        public DateTimeOffset ModifiedDate { get; internal set; }
    }
}