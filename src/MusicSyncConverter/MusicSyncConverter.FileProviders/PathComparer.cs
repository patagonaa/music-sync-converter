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
            if (_multipleSeparators)
            {
                x = x?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                y = y?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
            return _internalComparer.Equals(x, y);
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
