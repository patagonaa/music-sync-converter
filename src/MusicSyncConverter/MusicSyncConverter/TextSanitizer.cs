using MusicSyncConverter.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicSyncConverter
{
    public class TextSanitizer
    {
        public string SanitizeText(CharacterLimitations? config, string text, bool isPath, out bool hasUnsupportedChars)
        {
            if (config == null)
            {
                config = new CharacterLimitations();
            }

            if (isPath)
            {
                hasUnsupportedChars = false;
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
                        toReturn.Add(Sanitize(config, part, true, out var partHasUnsupportedChars));
                        hasUnsupportedChars |= partHasUnsupportedChars;
                    }
                }
                return string.Join(Path.DirectorySeparatorChar, toReturn);
            }
            else
            {
                return Sanitize(config, text, false, out hasUnsupportedChars);
            }
        }

        private static string Sanitize(CharacterLimitations config, string text, bool isForPath, out bool hasUnsupportedChars)
        {
            var toReturn = new StringBuilder();

            var pathUnsupportedChars = Path.GetInvalidFileNameChars();

            hasUnsupportedChars = false;

            var first = true;

            foreach (var inChar in text.EnumerateRunes())
            {
                string toInsert;

                var replacement = config.Replacements?.FirstOrDefault(x => x.Rune == inChar);
                if (replacement != null)
                {
                    toInsert = replacement.Replacement ?? string.Empty;
                }
                else if (NeedsNormalization(config.NormalizationMode, config.SupportedChars, inChar))
                {
                    toInsert = inChar.ToString().Normalize(NormalizationForm.FormKC);
                    if (config.NormalizationMode == UnicodeNormalizationMode.NonBmp && toInsert.Length > 1)
                        toInsert = "_";
                    hasUnsupportedChars = true;
                }
                else
                {
                    toInsert = inChar.ToString();
                }

                if (config.NormalizeCase && first)
                {
                    toInsert = new string(toInsert.EnumerateRunes().SelectMany((x, i) => i == 0 ? Rune.ToUpperInvariant(x).ToString() : x.ToString()).ToArray());
                    first = false;
                }

                foreach (var outChar in toInsert.EnumerateRunes())
                {
                    var outCharStr = outChar.ToString();

                    // if this is a path, replace chars that are invalid for path names
                    if (isForPath && outCharStr.Any(x => pathUnsupportedChars.Contains(x)))
                    {
                        hasUnsupportedChars = true;
                        toReturn.Append('_');
                    }
                    else if (config.SupportedChars != null && !config.SupportedChars.EnumerateRunes().Contains(outChar))
                    {
                        // we just accept our faith and insert the character anyways
                        hasUnsupportedChars = true;
                        toReturn.Append(outChar);
                    }
                    else
                    {
                        // char is supported
                        toReturn.Append(outChar);
                    }
                }
            }

            return toReturn.ToString();
        }

        private static bool NeedsNormalization(UnicodeNormalizationMode normalizationMode, string? supportedChars, Rune inChar)
        {
            return normalizationMode switch
            {
                UnicodeNormalizationMode.None => false,
                UnicodeNormalizationMode.NonBmp => !inChar.IsBmp,
                UnicodeNormalizationMode.Unsupported => supportedChars != null && !supportedChars.EnumerateRunes().Contains(inChar),
                UnicodeNormalizationMode.All => true,
                _ => throw new ArgumentOutOfRangeException(nameof(normalizationMode)),
            };
        }
    }
}
