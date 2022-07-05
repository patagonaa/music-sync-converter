using MusicSyncConverter.Models;
using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class SyncConfig
    {
        public IList<string> SourceExtensions { get; set; } =
            new List<string>
            {
                ".mp3",
                ".ogg",
                ".m4a",
                ".flac",
                ".opus",
                ".wma",
                ".wav",
                ".aac"
            };
        public TargetDeviceConfig DeviceConfig { get; set; } = null!;
        public IDictionary<string, FileFormatOverride>? PathFormatOverrides { get; set; }
        public string SourceDir { get; set; } = null!;
        public string TargetDir { get; set; } = null!;
        public IList<string>? Exclude { get; set; }
        public int? WorkersRead { get; set; }
        public int? WorkersConvert { get; set; }
        public int? WorkersWrite { get; set; }
    }
}
