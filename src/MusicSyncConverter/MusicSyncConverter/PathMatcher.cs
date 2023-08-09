using MusicSyncConverter.FileProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicSyncConverter
{
    // this might be replacable by Microsoft.Extensions.FileSystemGlobbing but I didn't know it existed at the time
    // of this implementation and while this implementation is ugly, it works for my cases
    public class PathMatcher
    {
        private static readonly string _pathSeperatorRegex;
        private static readonly string _notPathSeperatorRegex;

        private readonly ConcurrentDictionary<string, Regex> _globCache = new();
        private readonly bool _caseSensitive;

        static PathMatcher()
        {
            var escapedPathSeperators = new HashSet<string>
            {
                Regex.Escape(Path.DirectorySeparatorChar.ToString()),
                Regex.Escape(Path.AltDirectorySeparatorChar.ToString())
            };
            _pathSeperatorRegex = $"[{string.Join(string.Empty, escapedPathSeperators)}]";
            _notPathSeperatorRegex = $"[^{string.Join(string.Empty, escapedPathSeperators)}]";
        }

        public PathMatcher(bool caseSensitive)
        {
            _caseSensitive = caseSensitive;
        }

        public bool Matches(string glob, string path)
        {
            var regex = _globCache.GetOrAdd(glob, key => GetRegexForGlob(key, _caseSensitive));
            return regex.IsMatch(PathUtils.NormalizePath(path));
        }

        private static Regex GetRegexForGlob(string glob, bool caseSensitive)
        {
            if (glob.Contains("***"))
                throw new ArgumentException("only '*' and '**' wildcards are allowed");
            if (glob.Contains("::"))
                throw new ArgumentException("'::' not allowed in glob");
            var sb = new StringBuilder(glob);
            sb.Replace(Path.DirectorySeparatorChar.ToString(), "::pathseperator::");
            sb.Replace(Path.AltDirectorySeparatorChar.ToString(), "::pathseperator::");
            sb.Replace("**", "::wildcard::");
            sb.Replace("*", "::singlepartwildcard::");

            var globEscaped = Regex.Escape(sb.ToString());
            sb.Clear();
            sb.Append('^');
            sb.Append(globEscaped);
            sb.Append('$');

            sb.Replace("::wildcard::", ".*");
            sb.Replace("::singlepartwildcard::", $"{_notPathSeperatorRegex}*");
            sb.Replace("::pathseperator::", _pathSeperatorRegex);

            return new Regex(sb.ToString(), caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        }
    }
}
