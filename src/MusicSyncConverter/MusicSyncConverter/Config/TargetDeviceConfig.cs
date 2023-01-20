using MusicSyncConverter.Conversion;
using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class TargetDeviceConfig
    {
        public IList<FileFormatLimitation> SupportedFormats { get; set; } = null!;
        public EncoderInfo FallbackFormat { get; set; } = null!;
        public CharacterLimitations? CharacterLimitations
        {
            get
            {
                return PathCharacterLimitations == TagCharacterLimitations ? PathCharacterLimitations : null;
            }
            set
            {
                PathCharacterLimitations = value;
                TagCharacterLimitations = value;
            }
        }
        public CharacterLimitations? PathCharacterLimitations { get; set; }
        public CharacterLimitations? TagCharacterLimitations { get; set; }
        public bool ResolvePlaylists { get; set; } = false;
        public string? TagValueDelimiter { get; set; }
        public bool NormalizeCase { get; set; }
        public int? MaxDirectoryDepth { get; set; }
    }
}
