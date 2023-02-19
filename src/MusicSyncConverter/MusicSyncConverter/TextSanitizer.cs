﻿using MusicSyncConverter.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicSyncConverter
{
    public class TextSanitizer : ITextSanitizer
    {
        private readonly char[] _pathUnsupportedChars;

        public TextSanitizer()
        {
            var chars = "\"<>|:*?\\/";
            _pathUnsupportedChars = Path.GetInvalidFileNameChars()
                .Concat(chars)
                .Concat(Enumerable.Range(0, 32).Select(x => (char)x))
                .Distinct()
                .ToArray();
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

            var supportedRunes = config.SupportedChars?.EnumerateRunes().ToList();

            foreach (var inChar in text.EnumerateRunes())
            {
                string toInsert;

                var replacement = config.Replacements?.FirstOrDefault(x => x.Rune == inChar);
                if (replacement != null && (!isForPath || IsValidPath(replacement.Replacement)))
                {
                    toInsert = replacement.Replacement ?? string.Empty;
                }
                else if (NeedsNormalization(config.NormalizationMode, supportedRunes, inChar))
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
                    else if (supportedRunes != null && !supportedRunes.Contains(outChar))
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

        private bool IsValidPath(string? path)
        {
            if (path == null)
                return false;
            return path.All(x => !_pathUnsupportedChars.Contains(x));
        }

        private static bool NeedsNormalization(UnicodeNormalizationMode normalizationMode, IList<Rune>? supportedRunes, Rune inChar)
        {
            return normalizationMode switch
            {
                UnicodeNormalizationMode.None => false,
                UnicodeNormalizationMode.NonBmp => !inChar.IsBmp,
                UnicodeNormalizationMode.Unsupported => supportedRunes != null && !supportedRunes.Contains(inChar),
                UnicodeNormalizationMode.All => true,
                _ => throw new ArgumentOutOfRangeException(nameof(normalizationMode)),
            };
        }
    }
}
