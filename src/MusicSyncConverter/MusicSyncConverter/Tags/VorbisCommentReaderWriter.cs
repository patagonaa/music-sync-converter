using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class VorbisCommentReaderWriter : VorbisCommentReaderWriterBase
    {
        private static readonly bool _hasVorbisComment;

        static VorbisCommentReaderWriter()
        {
            _hasVorbisComment = CheckForVorbisComment();
        }

        public VorbisCommentReaderWriter(ITempFileSession tempFileSession)
            : base(tempFileSession)
        {
        }

        protected override async Task ExportTags(string tagFile, string fileName, CancellationToken cancellationToken)
        {
            var process = Process.Start("vorbiscomment", new string[] { "--raw", "--list", "--commentfile", tagFile, fileName });
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new Exception($"vorbiscomment exit code {process.ExitCode}");
        }

        protected override async Task ImportSafeTags(IReadOnlyList<KeyValuePair<string, string>> tags, bool overwrite, string fileName, CancellationToken cancellationToken)
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
            var process = Process.Start("vorbiscomment", new string[] { "--raw", overwrite ? "--write" : "--append", "--commentfile", tagsFile, fileName });
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new Exception($"vorbiscomment exit code {process.ExitCode}");
        }

        protected override async Task ImportUnsafeTag(KeyValuePair<string, string> tag, string fileName, CancellationToken cancellationToken)
        {
            // vorbiscomment does not seem to have a good way to import tags with line endings
            var safeTag = new KeyValuePair<string, string>(tag.Key, tag.Value.ReplaceLineEndings(" "));
            await ImportSafeTags(new List<KeyValuePair<string, string>> { safeTag }, false, fileName, cancellationToken);
        }

        public override bool CanHandle(string fileName, string fileExtension)
        {
            return _hasVorbisComment && (fileExtension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) && (Environment.OSVersion.Platform != PlatformID.Win32NT && fileExtension.Equals(".opus", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool CheckForVorbisComment()
        {
            try
            {
                Process.Start("vorbiscomment", "--version").WaitForExit();
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
