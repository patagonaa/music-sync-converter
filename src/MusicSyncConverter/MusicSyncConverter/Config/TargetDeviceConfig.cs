using MusicSyncConverter.Models;
using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class TargetDeviceConfig
    {
        public IList<FileFormat> SupportedFormats { get; set; }
        public EncoderInfo FallbackFormat { get; set; }
        public CharacterLimitations CharacterLimitations { get; set; }
        public FatSortMode FatSortMode { get; set; } = FatSortMode.None;
    }
}
