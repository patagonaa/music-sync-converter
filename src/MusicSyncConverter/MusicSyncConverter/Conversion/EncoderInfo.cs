namespace MusicSyncConverter.Conversion
{
    public class EncoderInfo
    {
        public string Extension { get; set; } = null!;
        public string Codec { get; set; } = null!;
        public string? Profile { get; set; }
        public int? Channels { get; set; }
        public int? SampleRateHz { get; set; }
        public string Muxer { get; set; } = null!;
        public string? AdditionalFlags { get; set; }
        public int? Bitrate { get; set; }
        public string? CoverCodec { get; set; }
        public int? MaxCoverSize { get; set; }

        public EncoderInfo Clone()
        {
            return (EncoderInfo)MemberwiseClone();
        }
    }
}
