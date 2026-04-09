using System.Collections.Generic;

namespace MusicSyncConverter.Config
{
    public class CharacterLimitations
    {
        public IList<string>? SupportedUnicodeRanges { get; set; }
        public string? SupportedChars { get; set; }
        public IList<CharReplacement>? Replacements { get; set; }
        public UnicodeNormalizationMode NormalizationMode { get; set; }
    }
}