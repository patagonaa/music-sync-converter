using MusicSyncConverter.Conversion;
using System.Collections.Generic;

namespace MusicSyncConverter.Config.OutputModels
{
    public class TargetDeviceConfig
    {
        public IList<FileFormatLimitation>? SupportedFormats { get; set; }
        required public EncoderInfo FallbackFormat { get; set; }
        public AlbumArtConfig? AlbumArt { get; set; }
        public CharacterLimitations? CharacterLimitations
        {
            set
            {
                PathCharacterLimitations ??= value;
                TagCharacterLimitations ??= value;
            }
        }
        public CharacterLimitations? PathCharacterLimitations { get; set; }
        public CharacterLimitations? TagCharacterLimitations { get; set; }
        public bool ResolvePlaylists { get; set; } = false;
        public string? TagValueDelimiter { get; set; }
        public bool NormalizeCase { get; set; } = false;
        public int? MaxDirectoryDepth { get; set; }
    }
}
