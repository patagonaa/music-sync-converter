using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MusicSyncConverter.Conversion.Ffmpeg
{
    public class FfProbeStream
    {
        [JsonPropertyName("codec_type")]
        public string CodecType { get; set; } = null!;

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("profile")]
        public string? Profile { get; set; }

        [JsonPropertyName("channels")]
        public int? Channels { get; set; }

        [JsonPropertyName("sample_rate")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? SampleRateHz { get; set; }

        [JsonPropertyName("bit_rate")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int BitRate { get; set; }

        [JsonPropertyName("tags")]
        public IDictionary<string, string>? Tags { get; set; }
    }
}
