namespace MusicSyncConverter.Config
{
    public class CharacterLimitations
    {
        public string[]? SupportedUnicodeRanges { get; set; }
        public string? SupportedChars { get; set; }
        public CharReplacement[]? Replacements { get; set; }
        public UnicodeNormalizationMode NormalizationMode { get; set; }
    }
}