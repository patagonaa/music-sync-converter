using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MusicSyncConverter.FileProviders
{
    public static class PathUtils
    {
        private static readonly bool _multipleSeparators;
        private static readonly string[] _levelChangeStrings;

        static PathUtils()
        {
            _multipleSeparators = Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar;

            var levelChangeStrings = new List<string>();
            levelChangeStrings.Add("." + Path.DirectorySeparatorChar);
            levelChangeStrings.Add(Path.DirectorySeparatorChar + ".");
            if (_multipleSeparators)
            {
                levelChangeStrings.Add("." + Path.AltDirectorySeparatorChar);
                levelChangeStrings.Add(Path.AltDirectorySeparatorChar + ".");
            }
            _levelChangeStrings = levelChangeStrings.ToArray();
        }

        public static bool HasPathSeperator(string? x)
        {
            return x != null && (x.Contains(Path.DirectorySeparatorChar) || (_multipleSeparators && x.Contains(Path.AltDirectorySeparatorChar)));
        }

        public static bool MayHaveLevelChange(string path)
        {
            return _levelChangeStrings.Any(x => path.Contains(x));
        }

        [return: NotNullIfNotNull("path")]
        public static string? NormalizePath(string? path)
        {
            if (path == null)
                return null;
            if (MayHaveLevelChange(path))
            {
                var stack = GetPathStack(path);
                var partsArray = stack.ToArray();
                Array.Reverse(partsArray);
                return string.Join(Path.DirectorySeparatorChar, partsArray);
            }
            else if (_multipleSeparators && path.Contains(Path.AltDirectorySeparatorChar))
            {
                return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
            else
            {
                return path;
            }
        }

        public static Stack<string> GetPathStack(string path)
        {
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

            return stack;
        }
    }
}
