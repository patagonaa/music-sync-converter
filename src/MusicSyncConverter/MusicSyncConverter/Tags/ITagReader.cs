using MusicSyncConverter.Conversion.Ffmpeg;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal interface ITagReader
    {
        /// <summary>
        /// Returns the files' tags.
        /// </summary>
        /// <param name="mediaAnalysis">The FFProbe analysis of the file</param>
        /// <param name="fileName">the file itself</param>
        /// <param name="fileExtension">the file's original extension</param>
        /// <returns>the files' tags in Vorbis Comment format</returns>
        Task<IReadOnlyList<KeyValuePair<string, string>>> GetTags(FfProbeResult mediaAnalysis, string fileName, string fileExtension, CancellationToken cancellationToken);
        bool CanHandle(string fileName, string fileExtension);
    }
}
