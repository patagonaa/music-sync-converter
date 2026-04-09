using MusicSyncConverter.Conversion;
using System.Collections.Generic;

namespace MusicSyncConverter.Config.InputModels
{
    public class InputSyncConfig
    {
        public IList<string>? SourceExtensions { get; set; }
        public InputTargetDeviceConfig? DeviceConfig { get; set; }
        public IDictionary<string, FileFormatOverride>? PathFormatOverrides { get; set; }
        public string? SourceDir { get; set; }
        public string? TargetDir { get; set; }
        public IList<string>? Exclude { get; set; }
        public IList<string>? KeepInTarget { get; set; }
        public int? WorkersRead { get; set; }
        public int? WorkersConvert { get; set; }
        public int? WorkersWrite { get; set; }
    }
}
