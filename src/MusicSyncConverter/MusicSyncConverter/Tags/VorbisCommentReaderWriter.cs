﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.Tags
{
    internal class VorbisCommentReaderWriter : VorbisCommentReaderWriterBase
    {
        private static readonly Regex _versionRegex = new Regex(@"(\d+.\d+(?:.\d+)?)", RegexOptions.Compiled);
        private static readonly Version? _vorbisCommentVersion;

        static VorbisCommentReaderWriter()
        {
            _vorbisCommentVersion = CheckForVorbisComment();
        }

        public VorbisCommentReaderWriter(ITempFileSession tempFileSession)
            : base(tempFileSession)
        {
        }

        protected override async Task ExportTags(string tagFile, string fileName, CancellationToken cancellationToken)
        {
            await ProcessStartHelper.RunProcess("vorbiscomment", new string[] { "--raw", "--list", "--commentfile", tagFile, fileName }, cancellationToken: cancellationToken);
        }

        public override async Task SetTags(IReadOnlyList<KeyValuePair<string, string>> tags, IReadOnlyList<AlbumArt> albumArt, string fileName, CancellationToken cancellationToken)
        {
            var tagsToSet = tags
                .Select(x => new KeyValuePair<string, string>(x.Key, EscapeUnsafeChars(x.Value)))
                .Concat(albumArt.Select(x => new KeyValuePair<string, string>("METADATA_BLOCK_PICTURE", x.ToVorbisMetaDataBlockPicture())))
                .ToList();

            var tagsFile = _tempFileSession.GetTempFilePath(".txt");
            using (var sw = new StreamWriter(tagsFile, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                foreach (var tag in tagsToSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await sw.WriteLineAsync($"{tag.Key}={tag.Value}");
                }
            }

            await ProcessStartHelper.RunProcess("vorbiscomment", new string[] { "--raw", "--escapes", "--write", "--commentfile", tagsFile, fileName }, cancellationToken: cancellationToken);
            File.Delete(tagsFile);
        }

        private static string EscapeUnsafeChars(string x)
        {
            return x.Replace(@"\", @"\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\0", "\\0");
        }

        public override bool CanHandle(string fileExtension)
        {
            if (fileExtension == ".ogg")
            {
                return _vorbisCommentVersion != null;
            }
            else
            {
                return false;
            }
        }

        private static Version? CheckForVorbisComment()
        {
            try
            {
                using var stderr = new StringWriter();
                try
                {
                    ProcessStartHelper.RunProcess("vorbiscomment", new[] { "--version" }, null, stderr).Wait();
                }
                catch (AggregateException ex)
                {
                    throw ex.InnerExceptions[0];
                }
                return Version.Parse(_versionRegex.Match(stderr.ToString()).Value);
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }
}
