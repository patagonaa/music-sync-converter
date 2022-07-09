using FFMpegCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class FfmpegTagReader : ITagReader
    {
        public Task<IReadOnlyList<KeyValuePair<string, string>>> GetTags(IMediaAnalysis mediaAnalysis, string filename, CancellationToken cancellationToken)
        {
            var toReturn = new List<KeyValuePair<string, string>>();
            foreach (var tag in mediaAnalysis.Format.Tags ?? new Dictionary<string, string>())
            {
                var vorbisKey = FfmpegTagKeyMapping.GetVorbisKey(tag.Key);
                if (vorbisKey == null)
                {
                    //TODO: Log
                    continue;
                }
                toReturn.Add(new KeyValuePair<string, string>(vorbisKey, tag.Value));
            }
            foreach (var tag in mediaAnalysis.PrimaryAudioStream?.Tags ?? new Dictionary<string, string>())
            {
                var vorbisKey = FfmpegTagKeyMapping.GetVorbisKey(tag.Key);
                if (vorbisKey == null)
                {
                    //TODO: Log
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
