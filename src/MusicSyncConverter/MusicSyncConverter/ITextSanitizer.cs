using MusicSyncConverter.Config;

namespace MusicSyncConverter
{
    public interface ITextSanitizer
    {
        string SanitizePathPart(CharacterLimitations? config, string part, out bool hasUnsupportedChars);
        string SanitizeText(CharacterLimitations? config, string text, out bool hasUnsupportedChars);
    }
}