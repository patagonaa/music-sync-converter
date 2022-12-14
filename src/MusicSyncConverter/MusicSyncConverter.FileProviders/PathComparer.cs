using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MusicSyncConverter.FileProviders
{
    public class PathComparer : IEqualityComparer<NormalizedPath>
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
            return Equals(x is null ? null : new NormalizedPath(x), y is null ? null : new NormalizedPath(y));
        }

        public bool FileNameEquals(string? x, string? y)
        {
            Debug.Assert(!PathUtils.HasPathSeperator(x) && !PathUtils.HasPathSeperator(y), "FileNameEquals may not be called with paths!");
            return _internalComparer.Equals(x, y);
        }

        public bool Equals(NormalizedPath? x, NormalizedPath? y)
        {
            return _internalComparer.Equals(x?.Path, y?.Path);
        }

        public int GetHashCode(NormalizedPath obj)
        {
            if (obj is null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            return _internalComparer.GetHashCode(obj.Path);
        }
    }
}
