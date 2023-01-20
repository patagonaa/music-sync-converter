using MusicSyncConverter.Config;
using MusicSyncConverter.FileProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicSyncConverter
{
    public class PathTransformer
    {
        private readonly ITextSanitizer _textSanitizer;

        public PathTransformer(ITextSanitizer textSanitizer)
        {
            _textSanitizer = textSanitizer;
        }

        public string TransformPath(string path, PathTransformType type, TargetDeviceConfig config, out bool pathIsUnsupported)
        {
            pathIsUnsupported = false;
            var toReturn = new List<string>();
            var pathParts = GetPathParts(path, type, config.MaxDirectoryDepth, out var partsReplaced);
            pathIsUnsupported |= partsReplaced;
            foreach (var pathPart in pathParts)
            {
                var sb = new StringBuilder();
                var first = true;
                foreach (var rune in pathPart.EnumerateRunes())
                {
                    if (first && config.NormalizeCase)
                    {
                        sb.Append(Rune.ToUpperInvariant(rune).ToString());
                        first = false;
                    }
                    else
                    {
                        sb.Append(rune.ToString());
                    }
                }

                toReturn.Add(_textSanitizer.SanitizePathPart(config.PathCharacterLimitations, sb.ToString(), out var partHasUnsupportedChars));
                pathIsUnsupported |= partHasUnsupportedChars;
            }
            return string.Join(Path.DirectorySeparatorChar, toReturn);
        }

        private static List<string> GetPathParts(string text, PathTransformType type, int? maxDirectoryDepth, out bool partsReplaced)
        {
            partsReplaced = false;

            var parts = PathUtils.GetPathStack(text);
            string? fileName = null;
            if (type == PathTransformType.FilePath)
            {
                fileName = parts.Pop();
            }

            while (maxDirectoryDepth.HasValue && parts.Count > maxDirectoryDepth)
            {
                partsReplaced |= true;
                var partRight = parts.Pop();
                var partLeft = parts.Pop();

                parts.Push(partLeft + "_" + partRight);
            }

            if (type == PathTransformType.FilePath)
            {
                parts.Push(fileName!);
            }

            return parts.Reverse().ToList();
        }
    }
}
