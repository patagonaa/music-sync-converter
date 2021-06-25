namespace MusicSyncConverter.Config
{
    public class FallbackFormat
    {
        public string Extension { get; set; }
        public string Muxer { get; set; }
        public string EncoderCodec { get; set; }
        public string EncoderProfile { get; set; }
        public int Bitrate { get; set; }
    }
}
