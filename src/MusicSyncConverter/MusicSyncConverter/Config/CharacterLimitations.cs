namespace MusicSyncConverter.Config
{
    public class CharacterLimitations
    {
        public string SupportedChars { get; set; }
        public CharReplacement[] Replacements { get; set; }
        public bool NormalizeCase { get; set; }
    }
}