using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
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
            glob = glob.Replace(Path.DirectorySeparatorChar.ToString(), "::pathseperator::");
            glob = glob.Replace(Path.AltDirectorySeparatorChar.ToString(), "::pathseperator::");
            glob = glob.Replace("**", "::wildcard::");
            glob = glob.Replace("*", "::singlepartwildcard::");
            var globEscaped = Regex.Escape(glob);
            globEscaped = globEscaped.Replace("::wildcard::", ".*");
            globEscaped = globEscaped.Replace("::singlepartwildcard::", $"{_notPathSeperatorRegex}*");
            globEscaped = globEscaped.Replace("::pathseperator::", _pathSeperatorRegex);

            var regexString = $"^{globEscaped}$";
            var regex = new Regex(regexString, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            return regex.IsMatch(path);
        }
    }
}
