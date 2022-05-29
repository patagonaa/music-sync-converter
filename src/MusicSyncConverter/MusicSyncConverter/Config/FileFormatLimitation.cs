using System;

namespace MusicSyncConverter.Config
{
    public class FileFormatLimitation
    {
        public string? Extension { get; set; }
        public string? Codec { get; set; }
        public string? Profile { get; set; }
        public int? MaxChannels { get; set; }
        public int? MaxSampleRateHz { get; set; }
        public int? MaxBitrate { get; set; }
        public FileFormatLimitation Clone()
        {
            return (FileFormatLimitation)this.MemberwiseClone();
        }
    }
}