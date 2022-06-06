using System;
using System.Collections.Generic;
using System.IO;

namespace MusicSyncConverter.FileProviders
{
    public class PathComparer : IEqualityComparer<string>
    {
        private static readonly bool _multipleSeparators = Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar;
        private readonly StringComparer _internalComparer;

        public PathComparer(bool caseSensitive)
        {
            _internalComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        }

        public bool Equals(string? x, string? y)
        {
            if (_internalComparer.Equals(x, y))
                return true;

            if ((HasPathSeperator(x) || HasPathSeperator(y)) && _internalComparer.Equals(NormalizePath(x), NormalizePath(y)))
                return true;
            return false;
        }

        private bool HasPathSeperator(string? x)
        {
            return x != null && (x.Contains(Path.DirectorySeparatorChar) || (_multipleSeparators && x.Contains(Path.AltDirectorySeparatorChar)));
        }

        private string? NormalizePath(string? path)
        {
            if (path == null)
                return null;
            var pathParts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stack = new Stack<string>(pathParts.Length);
            try
            {
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
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Invalid Path {path}", ex);
            }

            var partsArray = stack.ToArray();
            Array.Reverse(partsArray);
            return string.Join(Path.DirectorySeparatorChar, partsArray);
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
