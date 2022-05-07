namespace MusicSyncConverter.Config
{
    public class DeviceFileFormat
    {
        public string Extension { get; set; } = null!;
        public string Codec { get; set; } = null!;
        public string? Profile { get; set; }
        public int? MaxChannels { get; set; }
        public int? MaxSampleRateHz { get; set; }
    }
}