using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MusicSyncConverter.Conversion.Ffmpeg
{
    public class FfProbeResult
    {
        [JsonPropertyName("streams")]
        public IList<FfProbeStream> Streams { get; set; } = null!;

        [JsonPropertyName("format")]
        public FfProbeFormat Format { get; set; } = null!;
    }
}
