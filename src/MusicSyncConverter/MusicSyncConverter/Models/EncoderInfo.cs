namespace MusicSyncConverter.Models
{
    public class EncoderInfo : FileFormat
    {
        public string Muxer { get; set; }
        public string AdditionalFlags { get; set; }
        public int? Bitrate { get; set; }
        public string CoverCodec { get; set; }
        public int? MaxCoverSize { get; set; }
    }
}
