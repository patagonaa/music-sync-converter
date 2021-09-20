using MusicSyncConverter.Config;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicSyncConverter
{
    class TextSanitizer
    {
        public string SanitizeText(CharacterLimitations config, string text, bool isPath)
        {
            if (config == null)
                return text;
            var toReturn = new StringBuilder();

            var pathUnsupportedChars = Path.GetInvalidFileNameChars();

            var unsupportedChars = false;

            foreach (var inChar in text)
            {
                // path seperator always allowed for path
                if (isPath && (inChar == Path.DirectorySeparatorChar || inChar == '.'))
                {
                    toReturn.Append(inChar);
                    continue;
                }

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

                foreach (var outChar in toInsert)
                {

                    // if this is a path, replace chars that are invalid for path names
                    if (isPath && pathUnsupportedChars.Contains(outChar))
                    {
                        unsupportedChars = true;
                        toReturn.Append('_');
                    }
                    else if (!config.SupportedChars.Contains(outChar))
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
                //Console.WriteLine($"Warning: unsupported chars in {text}");
            }

            return toReturn.ToString();
        }
    }
}
