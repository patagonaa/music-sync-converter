using MusicSyncConverter.Config;

namespace MusicSyncConverter.Conversion
{
    public class FileFormatOverride : FileFormatLimitation
    {
        public string? Muxer { get; set; }

        public new FileFormatOverride Clone()
        {
            return (FileFormatOverride)MemberwiseClone();
        }
    }
}