using MusicSyncConverter.Models;
using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class TargetDeviceConfig
    {
        public IList<DeviceFileFormat> SupportedFormats { get; set; } = null!;
        public EncoderInfo FallbackFormat { get; set; } = null!;
        public CharacterLimitations? CharacterLimitations { get; set; }
    }
}
