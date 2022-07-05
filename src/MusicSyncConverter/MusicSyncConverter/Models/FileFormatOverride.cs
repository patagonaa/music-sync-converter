using MusicSyncConverter.Config;

namespace MusicSyncConverter.Models
{
    public class FileFormatOverride : FileFormatLimitation
    {
        public string? Muxer { get; set; }

        public new FileFormatOverride Clone()
        {
            return (FileFormatOverride)this.MemberwiseClone();
        }
    }
}