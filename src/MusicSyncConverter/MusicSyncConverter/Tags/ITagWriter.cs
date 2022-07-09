using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal interface ITagWriter
    {
        /// <summary>
        /// Sets the files' tags.
        /// </summary>
        /// <param name="tags">The tags in Vorbis Comment format</param>
        /// <param name="fileName">the file to set</param>
        Task SetTags(IReadOnlyList<KeyValuePair<string, string>> tags, string fileName, CancellationToken cancellationToken);
        bool CanHandle(string fileName, string fileExtension);
    }
}
