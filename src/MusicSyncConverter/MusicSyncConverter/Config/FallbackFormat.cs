namespace MusicSyncConverter.Config
{
    public class FallbackFormat
    {
        public string Extension { get; set; }
        public string Muxer { get; set; }
        public string EncoderCodec { get; set; }
        public string EncoderProfile { get; set; }
        public string AdditionalFlags { get; set; }
        public int Bitrate { get; set; }
        public string CoverCodec { get; set; }
        public int? MaxCoverSize { get; set; }
    }
}
