using FFMpegCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class FfmpegTagReader : ITagReader
    {
        private readonly ILogger _logger;
        private static readonly string[] _toSkip = new[] { "encoded_by", "major_brand", "minor_version", "compatible_brands", "handler_name", "vendor_id", "encoder", "creation_time" };

        public FfmpegTagReader(ILogger logger)
        {
            _logger = logger;
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>> GetTags(IMediaAnalysis mediaAnalysis, string filename, CancellationToken cancellationToken)
        {
            var toReturn = new List<KeyValuePair<string, string>>();
            foreach (var tag in mediaAnalysis.Format.Tags ?? new Dictionary<string, string>())
            {
                if (tag.Key.StartsWith("id3v2_priv.", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_toSkip.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                    continue;
                var vorbisKey = FfmpegTagKeyMapping.GetVorbisKey(tag.Key);
                if (vorbisKey == null)
                {
                    _logger.LogInformation("Could not map FFMPEG key {FfmpegKey} to Vorbis key", tag.Key);
                    continue;
                }
                toReturn.Add(new KeyValuePair<string, string>(vorbisKey, tag.Value));
            }
            foreach (var tag in mediaAnalysis.PrimaryAudioStream?.Tags ?? new Dictionary<string, string>())
            {
                if (tag.Key.StartsWith("id3v2_priv.", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_toSkip.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                    continue;
                var vorbisKey = FfmpegTagKeyMapping.GetVorbisKey(tag.Key);
                if (vorbisKey == null)
                {
                    _logger.LogInformation("Could not map FFMPEG key {FfmpegKey} to Vorbis key", tag.Key);
                    continue;
                }
                toReturn.Add(new KeyValuePair<string, string>(vorbisKey, tag.Value));
            }

            return Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(toReturn.ToList());
        }

        public bool CanHandle(string fileName, string fileExtension)
        {
            return true;
        }
    }
}
