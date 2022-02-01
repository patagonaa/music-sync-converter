﻿using MusicSyncConverter.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicSyncConverter
{
    public class TextSanitizer
    {
        public string SanitizeText(CharacterLimitations config, string text, bool isPath, ICollection<string> unsupportedStrings = null)
        {
            if (config == null)
                return text;

            if (isPath)
            {
                var parts = text.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var toReturn = new List<string>();
                foreach (var part in parts)
                {
                    if (part == ".." || part == ".")
                    {
                        toReturn.Add(part);
                    }
                    else
                    {
                        toReturn.Add(Sanitize(config, part, true, unsupportedStrings));
                    }
                }
                return string.Join(Path.DirectorySeparatorChar, toReturn);
            }
            else
            {
                return Sanitize(config, text, false, unsupportedStrings);
            }
        }

        private static string Sanitize(CharacterLimitations config, string text, bool isForPath, ICollection<string> unsupportedStrings)
        {
            var toReturn = new StringBuilder();

            var pathUnsupportedChars = Path.GetInvalidFileNameChars();

            var unsupportedChars = false;

            var first = true;

            foreach (var inChar in text)
            {
                string toInsert;
                var replacement = config.Replacements?.FirstOrDefault(x => x.Char == inChar);
                if (replacement != null)
                {
                    toInsert = replacement.Replacement;
                }
                else
                {
                    toInsert = inChar.ToString();
                }

                if (config.NormalizeCase && first)
                {
                    toInsert = new string(toInsert.SelectMany((x, i) => i == 0 ? x.ToString().ToUpperInvariant() : x.ToString()).ToArray());
                    first = false;
                }

                foreach (var outChar in toInsert)
                {

                    // if this is a path, replace chars that are invalid for path names
                    if (isForPath && pathUnsupportedChars.Contains(outChar))
                    {
                        unsupportedChars = true;
                        toReturn.Append('_');
                    }
                    else if (config.SupportedChars != null && !config.SupportedChars.Contains(outChar))
                    {
                        // we just accept our faith and insert the character anyways
                        unsupportedChars = true;
                        toReturn.Append(outChar);
                    }
                    else
                    {
                        // char is supported
                        toReturn.Append(outChar);
                    }
                }
            }

            if (unsupportedChars)
            {
                unsupportedStrings?.Add(text);
                //Console.WriteLine($"Warning: unsupported chars in {text}");
            }

            return toReturn.ToString();
        }
    }
}
