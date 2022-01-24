namespace MusicSyncConverter.Config
{
    public class DeviceFileFormat
    {
        public string Extension { get; set; }
        public string Codec { get; set; }
        public string Profile { get; set; }
        public int? MaxChannels { get; set; }
        public int? MaxSampleRateHz { get; set; }
    }
}