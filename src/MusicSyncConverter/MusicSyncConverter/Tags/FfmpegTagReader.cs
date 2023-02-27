using Microsoft.Extensions.Logging;
using MusicSyncConverter.Conversion.Ffmpeg;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class FfmpegTagReader : ITagReader
    {
        private readonly FfmpegTagMapper _tagMapper;

        public FfmpegTagReader(ILogger logger)
        {
            _tagMapper = new FfmpegTagMapper(logger);
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>> GetTags(FfProbeResult mediaAnalysis, string filename, string fileExtension, CancellationToken cancellationToken)
        {
            var toReturn = new List<KeyValuePair<string, string>>();
            if (mediaAnalysis.Format.Tags != null)
                toReturn.AddRange(_tagMapper.GetVorbisFromFfmpeg(mediaAnalysis.Format.Tags, fileExtension));

            var audioStreamTags = mediaAnalysis.Streams.SingleOrDefault(x => x.CodecType == FfProbeCodecType.Audio)?.Tags;
            if (audioStreamTags != null)
                toReturn.AddRange(_tagMapper.GetVorbisFromFfmpeg(audioStreamTags, fileExtension));

            return Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(toReturn.ToList());
        }

        public bool CanHandle(string fileExtension)
        {
            return true;
        }
    }
}
