using MusicSyncConverter.Conversion.Ffmpeg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal abstract class VorbisCommentReaderWriterBase : ITagReader, ITagWriter
    {
        protected readonly ITempFileSession _tempFileSession;
        private readonly Regex _keyValueRegex = new Regex("^([a-zA-Z ]+)=(.+)$");

        public VorbisCommentReaderWriterBase(ITempFileSession tempFileSession)
        {
            _tempFileSession = tempFileSession;
        }

        public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTags(FfProbeResult mediaAnalysis, string fileName, string fileExtension, CancellationToken cancellationToken)
        {
            var tagFile = _tempFileSession.GetTempFilePath();
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
                        tags.Add(new KeyValuePair<string, string>(match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value));
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

        public async Task SetTags(IReadOnlyList<KeyValuePair<string, string>> tags, string fileName, CancellationToken cancellationToken)
        {
            var unsafeChars = new char[] { '\r', '\n' };
            var safeTags = tags.Where(tag => !unsafeChars.Any(unsafeChar => tag.Value.Contains(unsafeChar))).ToList();
            var unsafeTags = tags.Except(safeTags).ToList();
            await ImportSafeTags(safeTags, true, fileName, cancellationToken);

            foreach (var tag in unsafeTags)
            {
                await ImportUnsafeTag(tag, fileName, cancellationToken);
            }
        }
        protected abstract Task ExportTags(string tagFile, string fileName, CancellationToken cancellationToken);
        protected abstract Task ImportSafeTags(IReadOnlyList<KeyValuePair<string, string>> tags, bool overwrite, string fileName, CancellationToken cancellationToken);
        protected abstract Task ImportUnsafeTag(KeyValuePair<string, string> tag, string fileName, CancellationToken cancellationToken);
        public abstract bool CanHandle(string fileName, string fileExtension);
    }
}
