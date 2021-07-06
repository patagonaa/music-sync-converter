using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class TargetDeviceConfig
    {
        public IList<FileFormat> SupportedFormats { get; set; }
        public FallbackFormat FallbackFormat { get; set; }
        public CharacterLimitations CharacterLimitations { get; set; }
    }
}
