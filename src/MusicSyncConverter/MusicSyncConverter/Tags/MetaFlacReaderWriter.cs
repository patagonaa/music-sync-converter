using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class MetaFlacReaderWriter : VorbisCommentReaderWriterBase
    {
        private static readonly bool _hasMetaFlac;

        static MetaFlacReaderWriter()
        {
            _hasMetaFlac = CheckForMetaFlac();
        }

        public MetaFlacReaderWriter(ITempFileSession tempFileSession)
            : base(tempFileSession)
        {
        }

        protected override async Task ExportTags(string tagFile, string fileName, CancellationToken cancellationToken)
        {
            var process = Process.Start("metaflac", new string[] { "--no-utf8-convert", $"--export-tags-to={tagFile}", fileName });
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new Exception($"metaflac exit code {process.ExitCode}");
        }

        public override async Task SetTags(IReadOnlyList<KeyValuePair<string, string>> tags, IReadOnlyList<AlbumArt> albumArt, string fileName, CancellationToken cancellationToken)
        {
            IReadOnlyList<KeyValuePair<string, string>> tagsToWrite = tags
                .Concat(albumArt.Select(x => new KeyValuePair<string, string>("METADATA_BLOCK_PICTURE", Convert.ToBase64String(GetMetadataBlockPicture(x)))))
                .ToList();

            var unsafeChars = new char[] { '\r', '\n' };
            var safeTags = tagsToWrite.Where(tag => !unsafeChars.Any(unsafeChar => tag.Value.Contains(unsafeChar))).ToList();
            var unsafeTags = tags.Except(safeTags).ToList();
            await ImportSafeTags(safeTags, true, fileName, cancellationToken);

            foreach (var tag in unsafeTags)
            {
                await ImportUnsafeTag(tag, fileName, cancellationToken);
            }
        }

        protected async Task ImportSafeTags(IReadOnlyList<KeyValuePair<string, string>> tags, bool overwrite, string fileName, CancellationToken cancellationToken)
        {
            string tagsFile = _tempFileSession.GetTempFilePath();
            using (var sw = new StreamWriter(tagsFile, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                foreach (var tag in tags)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await sw.WriteLineAsync($"{tag.Key}={tag.Value}");
                }
            }
            var args = new List<string>
            {
                "--no-utf8-convert"
            };
            if (overwrite)
                args.Add("--remove-all-tags");
            args.Add($"--import-tags-from={tagsFile}");
            args.Add(fileName);

            var process = Process.Start("metaflac", args);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new Exception($"metaflac exit code {process.ExitCode}");
        }

        protected async Task ImportUnsafeTag(KeyValuePair<string, string> tag, string fileName, CancellationToken cancellationToken)
        {
            var tagFile = _tempFileSession.GetTempFilePath();
            await File.WriteAllTextAsync(tagFile, tag.Value.ReplaceLineEndings(), new UTF8Encoding(false), cancellationToken);
            var process = Process.Start("metaflac", new string[] { "--no-utf8-convert", $"--set-tag-from-file={tag.Key}={tagFile}", fileName });
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new Exception($"metaflac exit code {process.ExitCode}");
        }

        public override bool CanHandle(string fileExtension)
        {
            return _hasMetaFlac && fileExtension.Equals(".flac", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CheckForMetaFlac()
        {
            try
            {
                ProcessStartHelper.RunProcess("metaflac", new[] { "--version" }).Wait();
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }
    }
}
