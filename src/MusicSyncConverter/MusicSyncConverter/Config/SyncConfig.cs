using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class SyncConfig
    {
        public TargetDeviceConfig DeviceConfig { get; set; }
        public string SourceDir { get; set; }
        public string TargetDir { get; set; }
        public IList<string> Exclude { get; set; }
        public int WorkersConvert { get; set; }
        public int WorkersWrite { get; set; }
    }
}
