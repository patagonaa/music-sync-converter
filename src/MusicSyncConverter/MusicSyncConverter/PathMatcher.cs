using System;
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
        private readonly string _pathSeperatorRegex;
        private readonly string _notPathSeperatorRegex;

        public PathMatcher()
        {
            var escapedPathSeperators = new HashSet<string>
            {
                Regex.Escape(Path.DirectorySeparatorChar.ToString()),
                Regex.Escape(Path.AltDirectorySeparatorChar.ToString())
            };

            _pathSeperatorRegex = $"[{string.Join(string.Empty, escapedPathSeperators)}]";
            _notPathSeperatorRegex = $"[^{string.Join(string.Empty, escapedPathSeperators)}]";
        }

        public bool Matches(string glob, string path, bool caseSensitive)
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

            var regex = new Regex(sb.ToString(), caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            return regex.IsMatch(path);
        }
    }
}
