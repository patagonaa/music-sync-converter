using MusicSyncConverter.Config;
using NUnit.Framework;

namespace MusicSyncConverter.UnitTests
{
    public class TextSanitizerTests
    {
        private readonly ITextSanitizer _sut;

        public TextSanitizerTests()
        {
            _sut = new TextSanitizer();
        }

        [TestCase("!§$%&/()=?*\\")]
        public void Test_NoLimitations_Text_NoChange(string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = null,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, out _);
            Assert.AreEqual(text, result);
        }

        [TestCase("!§$%&()=_", "!§$%&()=\0")]
        public void Test_NoLimitations_Path_InvalidCharsReplaced(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = null,
                SupportedChars = null
            };
            var result = _sut.SanitizePathPart(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("aeoeueAeOeUess<3x", "äöüÄÖÜß♥×")]
        public void Test_Limitations_Text_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = new[]
                {
                    new CharReplacement{ Char = "ä", Replacement ="ae" },
                    new CharReplacement{ Char = "ö", Replacement ="oe" },
                    new CharReplacement{ Char = "ü", Replacement ="ue" },
                    new CharReplacement{ Char = "Ä", Replacement ="Ae" },
                    new CharReplacement{ Char = "Ö", Replacement ="Oe" },
                    new CharReplacement{ Char = "Ü", Replacement ="Ue" },
                    new CharReplacement{ Char = "ß", Replacement ="ss" },
                    new CharReplacement{ Char = "♥", Replacement ="<3" },
                    new CharReplacement{ Char = "×", Replacement ="x" }
                },
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("2𝟘𝟙X", "𝟚𝟘𝟙𝕏")]
        public void Test_Limitations_SurrogateText_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = new[]
                {
                    new CharReplacement{ Char = "𝟚", Replacement ="2" },
                    new CharReplacement{ Char = "𝕏", Replacement ="X" },
                },
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("201X", "𝟚𝟘𝟙𝕏")]
        [TestCase("201XABC", "𝟚𝟘𝟙𝕏ABC")]
        [TestCase("201X²³", "𝟚𝟘𝟙𝕏²³")]
        [TestCase("_", "𓃒")]
        public void Test_Limitations_NormalizeNonBmp_ReplaceNonBmpChars(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = null,
                NormalizationMode = UnicodeNormalizationMode.NonBmp,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("VIII", "Ⅷ", "")]
        [TestCase("ABC", "ABC", "")]
        [TestCase("4,63 × 10¹⁷⁰", "4,63 × 10¹⁷⁰", "×¹⁷⁰")]
        [TestCase("𓃒", "𓃒", "")]
        [TestCase("/", "／", "")]
        public void Test_Limitations_NormalizeUnsupported_ReplaceUnsupportedChars(string expected, string text, string supportedChars)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = null,
                NormalizationMode = UnicodeNormalizationMode.Unsupported,
                SupportedChars = supportedChars
            };
            var result = _sut.SanitizeText(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("02 ♫ II／ The SALON.flac", "02 ♫ Ⅱ／ The SALON.flac")]
        [TestCase("07 Rod ♥ You.flac", "07 Rod ♥ You.flac")]
        public void Test_Limitations_NormalizeUnsupported_DontCreatePathInvalidChar(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = new CharReplacement[]
                {
                    new CharReplacement{ Char = "♥", Replacement = "<3" }
                },
                NormalizationMode = UnicodeNormalizationMode.Unsupported,
                SupportedChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz."
            };
            var result = _sut.SanitizePathPart(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }

        [Ignore("this is kinda messy to handle so I don't care")]
        [TestCase("testCombinedEmoji123", "test👨‍👩‍👧‍👦123")]
        public void Test_Limitations_CombinedEmojiText_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                Replacements = new[]
                {
                    new CharReplacement{ Char = "👨‍👩‍👧‍👦", Replacement ="WeirdEmoji" },
                },
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, out _);
            Assert.AreEqual(expected, result);
        }
    }
}