using MusicSyncConverter.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Unicode;

namespace MusicSyncConverter
{
    public class TextSanitizer : ITextSanitizer
    {
        private static readonly char[] _pathUnsupportedChars;
        private static readonly Dictionary<string, (int Start, int End)> _unicodeRanges;

        static TextSanitizer()
        {
            var chars = "\"<>|:*?\\/";
            _pathUnsupportedChars = Path.GetInvalidFileNameChars()
                .Concat(chars)
                .Concat(Enumerable.Range(0, 32).Select(x => (char)x))
                .Distinct()
                .ToArray();

            _unicodeRanges = new Dictionary<string, (int Start, int End)>();
            var rangeProperties = typeof(UnicodeRanges).GetProperties(BindingFlags.Public | BindingFlags.Static);
            foreach (var rangeProp in rangeProperties)
            {
                var range = (UnicodeRange?)rangeProp.GetValue(null);
                if (range != null)
                    _unicodeRanges[rangeProp.Name] = (range.FirstCodePoint, range.FirstCodePoint + range.Length - 1);
            }
            _unicodeRanges["EgyptianHieroglyphs"] = (0x13000, 0x1342F);
            _unicodeRanges["MathematicalAlphanumericSymbols"] = (0x1D400, 0x1D7FF);
        }

        public string SanitizePathPart(CharacterLimitations? config, string part, out bool hasUnsupportedChars)
        {
            hasUnsupportedChars = false;

            if (part == ".." || part == ".")
            {
                return part;
            }
            else
            {
                return Sanitize(config, part, true, out hasUnsupportedChars);
            }
        }

        public string SanitizeText(CharacterLimitations? config, string text, out bool hasUnsupportedChars)
        {
            return Sanitize(config, text, false, out hasUnsupportedChars);
        }

        private string Sanitize(CharacterLimitations? config, string text, bool isForPath, out bool hasUnsupportedChars)
        {
            config ??= new CharacterLimitations();

            var toReturn = new StringBuilder();

            hasUnsupportedChars = false;

            foreach (var inChar in text.EnumerateRunes())
            {
                string toInsert;

                var replacement = config.Replacements?.FirstOrDefault(x => x.Rune == inChar);
                if (replacement != null && (!isForPath || IsValidPath(replacement.Replacement)))
                {
                    toInsert = replacement.Replacement ?? string.Empty;
                }
                else if (NeedsNormalization(config, inChar))
                {
                    var normalized = inChar.ToString().Normalize(NormalizationForm.FormKC);
                    if (!isForPath || IsValidPath(normalized))
                    {
                        toInsert = normalized;
                    }
                    else
                    {
                        toInsert = inChar.ToString();
                    }

                    if (config.NormalizationMode == UnicodeNormalizationMode.NonBmp && toInsert.Length > 1)
                        toInsert = "_";
                    hasUnsupportedChars = true;
                }
                else
                {
                    toInsert = inChar.ToString();
                }

                foreach (var outChar in toInsert.EnumerateRunes())
                {
                    var outCharStr = outChar.ToString();

                    // if this is a path, replace chars that are invalid for path names
                    if (isForPath && !IsValidPath(outCharStr))
                    {
                        hasUnsupportedChars = true;
                        toReturn.Append('_');
                    }
                    else if (!IsSupportedRune(outChar, config))
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

        private static bool IsSupportedRune(Rune rune, CharacterLimitations characterLimitations)
        {
            if (characterLimitations.SupportedChars == null && characterLimitations.SupportedUnicodeRanges == null)
                return true;

            if (characterLimitations.SupportedChars?.Contains(rune.ToString()) ?? false)
            {
                return true;
            }
            foreach (var unicodeRangeName in characterLimitations.SupportedUnicodeRanges ?? Array.Empty<string>())
            {
                if (!_unicodeRanges.TryGetValue(unicodeRangeName, out var unicodeRange))
                    throw new ArgumentException($"Invalid Unicode Range {unicodeRangeName}");

                if (rune.Value >= unicodeRange.Start && rune.Value <= unicodeRange.End)
                    return true;
            }

            return false;
        }

        private static bool IsValidPath(string? path)
        {
            if (path == null)
                return false;
            return path.All(x => !_pathUnsupportedChars.Contains(x));
        }

        private static bool NeedsNormalization(CharacterLimitations characterLimitations, Rune inChar)
        {
            return characterLimitations.NormalizationMode switch
            {
                UnicodeNormalizationMode.None => false,
                UnicodeNormalizationMode.NonBmp => !inChar.IsBmp,
                UnicodeNormalizationMode.Unsupported => !IsSupportedRune(inChar, characterLimitations),
                UnicodeNormalizationMode.All => true,
                _ => throw new ArgumentOutOfRangeException(nameof(characterLimitations)),
            };
        }
    }
}
