using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace MusicSyncConverter
{
    internal class PathComparer : IEqualityComparer<string>
    {
        private readonly bool _multipleSeparators;
        private readonly StringComparer _internalComparer;

        public PathComparer(bool caseSensitive)
        {
            _multipleSeparators = Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar;
            _internalComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        }

        public bool Equals(string x, string y)
        {
            if (_multipleSeparators)
            {
                x = x?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                y = y?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
            return _internalComparer.Equals(x, y);
        }

        public int GetHashCode([DisallowNull] string obj)
        {
            if (_multipleSeparators)
            {
                obj = obj?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
            return obj.GetHashCode();
        }
    }
}
