using System;
using System.Collections.Generic;
using System.IO;

namespace MusicSyncConverter.FileProviders
{
    public class PathComparer : IEqualityComparer<string>
    {
        private readonly bool _multipleSeparators;
        private readonly StringComparer _internalComparer;

        public PathComparer(bool caseSensitive)
        {
            _multipleSeparators = Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar;
            _internalComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        }

        public bool Equals(string? x, string? y)
        {
            return _internalComparer.Equals(NormalizePath(x), NormalizePath(y));
        }

        private string? NormalizePath(string? path)
        {
            if(path == null)
                return null;
            var pathParts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stack = new Stack<string>();
            foreach (var part in pathParts)
            {
                if (part == "..")
                {
                    stack.Pop();
                }
                else if (part == ".")
                {
                    continue;
                }
                else
                {
                    stack.Push(part);
                }
            }
            return string.Join(Path.DirectorySeparatorChar, stack);
        }

        public int GetHashCode(string? obj)
        {
            if (_multipleSeparators)
            {
                obj = obj?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
            return obj?.GetHashCode() ?? 0;
        }
    }
}
