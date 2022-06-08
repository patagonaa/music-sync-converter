using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicSyncConverter.FileProviders
{
    public class PathComparer : IEqualityComparer<string>
    {
        private readonly StringComparer _internalComparer;

        public PathComparer(bool caseSensitive)
        {
            _internalComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        }

        public bool Equals(string? x, string? y)
        {
            if (_internalComparer.Equals(x, y))
                return true;
            return _internalComparer.Equals(PathUtils.NormalizePath(x), PathUtils.NormalizePath(y));
        }

        public bool FileNameEquals(string? x, string? y)
        {
#if DEBUG
            if (PathUtils.HasPathSeperator(x) || PathUtils.HasPathSeperator(y))
                throw new ArgumentException("FileNameEquals may not be called with paths!");
#endif
            return _internalComparer.Equals(x, y);
        }

        public int GetHashCode(string? obj)
        {
            if (obj == null)
                return 0;

            return _internalComparer.GetHashCode(PathUtils.NormalizePath(obj));
        }
    }
}
