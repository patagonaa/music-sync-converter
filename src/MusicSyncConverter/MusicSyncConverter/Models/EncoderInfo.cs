namespace MusicSyncConverter.Models
{
    public class EncoderInfo
    {
        public string Extension { get; set; }
        public string Codec { get; set; }
        public string Profile { get; set; }
        public int? Channels { get; set; }
        public int? SampleRateHz { get; set; }
        public string Muxer { get; set; }
        public string AdditionalFlags { get; set; }
        public int? Bitrate { get; set; }
        public string CoverCodec { get; set; }
        public int? MaxCoverSize { get; set; }
    }
}
