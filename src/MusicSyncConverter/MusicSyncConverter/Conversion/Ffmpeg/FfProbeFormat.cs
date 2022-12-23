using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MusicSyncConverter.Conversion.Ffmpeg
{
    public class FfProbeFormat
    {
        [JsonPropertyName("format_name")]
        public string? FormatName { get; set; }
        [JsonPropertyName("tags")]
        public IDictionary<string, string>? Tags { get; set; }
    }
}
