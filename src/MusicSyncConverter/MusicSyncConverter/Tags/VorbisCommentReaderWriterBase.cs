using MusicSyncConverter.Conversion.Ffmpeg;
using System;
using System.Buffers.Binary;
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
                while (!sr.EndOfStream)
                {
                    string? line = await ReadLine(sr);
                    if (line == null)
                        break;

                    var match = _keyValueRegex.Match(line);
                    if (match.Success)
                    {
                        string key = match.Groups[1].Value.ToUpperInvariant();
                        if (key == "METADATA_BLOCK_PICTURE")
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

        private static async Task<string?> ReadLine(StreamReader sr)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // both vorbiscomment and metaflac write the tag file in text mode which converts \n to \r\n
                // but this also means a tag containing \r\n is converted to \r\r\n which messes things up, so we just ignore all \r and split by \n only

                if (sr.EndOfStream)
                    return null;
                var sb = new StringBuilder();
                int chr;
                while ((chr = sr.Read()) != -1)
                {
                    if (chr == '\r')
                        continue;
                    if (chr == '\n')
                        break;
                    sb.Append((char)chr);
                }
                return sb.ToString();
            }
            return await sr.ReadLineAsync();
        }

        protected abstract Task ExportTags(string tagFile, string fileName, CancellationToken cancellationToken);
        public abstract bool CanHandle(string fileExtension);
        public abstract Task SetTags(IReadOnlyList<KeyValuePair<string, string>> tags, IReadOnlyList<AlbumArt> albumArt, string fileName, CancellationToken cancellationToken);
    }
}
