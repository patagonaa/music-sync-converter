using MusicSyncConverter.Models;
using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class TargetDeviceConfig
    {
        public IList<FileFormatLimitation> SupportedFormats { get; set; } = null!;
        public EncoderInfo FallbackFormat { get; set; } = null!;
        public CharacterLimitations? CharacterLimitations { get; set; }
        public bool ResolvePlaylists { get; set; } = false;
    }
}
