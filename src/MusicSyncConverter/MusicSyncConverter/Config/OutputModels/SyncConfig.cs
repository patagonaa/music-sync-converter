using MusicSyncConverter.Conversion;
using System.Collections.Generic;

namespace MusicSyncConverter.Config.OutputModels
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
                ".aac",
                ".mod",
                ".it",
                ".xm"
            };
        required public TargetDeviceConfig DeviceConfig { get; set; }
        required public IDictionary<string, FileFormatOverride> PathFormatOverrides { get; set; }
        required public string SourceDir { get; set; }
        required public string TargetDir { get; set; }
        required public IList<string> Exclude { get; set; }
        required public IList<string> KeepInTarget { get; set; }
        public int? WorkersRead { get; set; }
        public int? WorkersConvert { get; set; }
        public int? WorkersWrite { get; set; }
    }
}
