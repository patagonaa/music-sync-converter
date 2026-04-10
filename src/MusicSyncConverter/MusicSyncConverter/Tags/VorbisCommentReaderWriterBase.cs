using MusicSyncConverter.Conversion.Ffmpeg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal abstract class VorbisCommentReaderWriterBase : ITagReader, ITagWriter
    {
        protected readonly ITempFileSession _tempFileSession;
        private readonly Regex _keyValueRegex = new Regex("^([^=\n\r]+)=(.*)$");

        public VorbisCommentReaderWriterBase(ITempFileSession tempFileSession)
        {
            _tempFileSession = tempFileSession;
        }

        public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTags(FfProbeResult mediaAnalysis, string fileName, string fileExtension, CancellationToken cancellationToken)
        {
            var tagFile = _tempFileSession.GetTempFilePath(".txt");
            await ExportTags(tagFile, fileName, cancellationToken);

            var tags = new List<KeyValuePair<string, string>>();
            using (var sr = new StreamReader(tagFile, Encoding.UTF8))
            {
                string? line;
                while ((line = await ReadLine(sr, cancellationToken)) != null)
                {
                    var match = _keyValueRegex.Match(line);
                    if (match.Success)
                    {
                        string key = match.Groups[1].Value.ToUpperInvariant();
                        if (key == AlbumArt.VorbisMetadataKey)
                            continue;
                        tags.Add(new KeyValuePair<string, string>(key, match.Groups[2].Value));
                    }
                    else
                    {
                        if (tags.Count == 0)
                            continue;
                        var lastTag = tags[^1];
                        tags[^1] = new KeyValuePair<string, string>(lastTag.Key, lastTag.Value + "\n" + line);
                    }
                }
            }
            File.Delete(tagFile);

            return tags;
        }

        private static async Task<string?> ReadLine(StreamReader sr, CancellationToken token)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // both vorbiscomment and metaflac write the tag file in text mode which converts \n to \r\n
                // but this also means a tag containing \r\n is converted to \r\r\n which messes things up, so we just ignore all \r and split by \n only
                return (await sr.ReadLineAsync(token))?.Replace("\r", null);
            }
            return await sr.ReadLineAsync(token);
        }

        protected abstract Task ExportTags(string tagFile, string fileName, CancellationToken cancellationToken);
        public abstract bool CanHandle(string muxer);
        public abstract Task SetTags(IReadOnlyList<KeyValuePair<string, string>> tags, IReadOnlyList<AlbumArt> albumArt, string fileName, CancellationToken cancellationToken);
    }
}
