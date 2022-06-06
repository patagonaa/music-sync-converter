using MusicSyncConverter.Config;
using NUnit.Framework;
using System.IO;

namespace MusicSyncConverter.UnitTests
{
    public class TextSanitizerTests
    {
        private TextSanitizer _sut;

        [SetUp]
        public void Setup()
        {
            _sut = new TextSanitizer();
        }

        [TestCase("!§$%&/()=?*\\")]
        public void Test_NoLimitations_Text_NoChange(string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = false,
                Replacements = null,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(text, result);
        }

        [TestCase("!§$%&()=_", "!§$%&()=\0")]
        public void Test_NoLimitations_Path_InvalidCharsReplaced(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = false,
                Replacements = null,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, true, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("aeoeueAeOeUess<3x", "äöüÄÖÜß♥×")]
        public void Test_Limitations_Text_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = false,
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
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("2𝟘𝟙X", "𝟚𝟘𝟙𝕏")]
        public void Test_Limitations_SurrogateText_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = false,
                Replacements = new[]
                {
                    new CharReplacement{ Char = "𝟚", Replacement ="2" },
                    new CharReplacement{ Char = "𝕏", Replacement ="X" },
                },
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
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
                NormalizeCase = false,
                Replacements = null,
                NormalizationMode = UnicodeNormalizationMode.NonBmp,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("VIII", "Ⅷ", "")]
        [TestCase("ABC", "ABC", "")]
        [TestCase("4,63 × 10¹⁷⁰", "4,63 × 10¹⁷⁰", "×¹⁷⁰")]
        [TestCase("𓃒", "𓃒", "")]
        public void Test_Limitations_NormalizeUnsupported_ReplaceUnsupportedChars(string expected, string text, string supportedChars)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = false,
                Replacements = null,
                NormalizationMode = UnicodeNormalizationMode.Unsupported,
                SupportedChars = supportedChars
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(expected, result);
        }

        [Ignore("this is kinda messy to handle so I don't care")]
        [TestCase("testCombinedEmoji123", "test👨‍👩‍👧‍👦123")]
        public void Test_Limitations_CombinedEmojiText_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = false,
                Replacements = new[]
                {
                    new CharReplacement{ Char = "👨‍👩‍👧‍👦", Replacement ="WeirdEmoji" },
                },
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("Hallo Welt!", "Hallo Welt!")]
        [TestCase("Hallo Welt!", "hallo Welt!")]
        [TestCase("", "")]
        [TestCase("Toelle Tesstdaten", "Tölle Teßtdaten")]
        [TestCase("Oelle Tesstdaten", "ölle Teßtdaten")]
        [TestCase("Oelle Tesstdaten", "Ölle Teßtdaten")]
        [TestCase("Ssupergeil", "ßupergeil")]
        [TestCase("Ssupergeil", "ẞupergeil")]
        public void Test_Limitations_Text_NormalizeCase_Replacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = true,
                Replacements = new[]
                {
                    new CharReplacement{ Char = "ä", Replacement ="ae" },
                    new CharReplacement{ Char = "ö", Replacement ="oe" },
                    new CharReplacement{ Char = "ü", Replacement ="ue" },
                    new CharReplacement{ Char = "Ä", Replacement ="Ae" },
                    new CharReplacement{ Char = "Ö", Replacement ="Oe" },
                    new CharReplacement{ Char = "Ü", Replacement ="Ue" },
                    new CharReplacement{ Char = "ß", Replacement ="ss" },
                    new CharReplacement{ Char = "ẞ", Replacement ="ss" },
                    new CharReplacement{ Char = "♥", Replacement ="<3" },
                    new CharReplacement{ Char = "×", Replacement ="x" }
                },
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("Hallo Welt!", "Hallo Welt!")]
        [TestCase("Hallo Welt!", "hallo Welt!")]
        [TestCase("", "")]
        [TestCase("Tölle Testdaten", "Tölle Testdaten")]
        [TestCase("Ölle Testdaten", "ölle Testdaten")]
        [TestCase("Ölle Testdaten", "Ölle Testdaten")]
        public void Test_NoLimitations_Text_NormalizeCase_NoReplacements(string expected, string text)
        {
            var limitations = new CharacterLimitations
            {
                NormalizeCase = true,
                Replacements = null,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, false, out _);
            Assert.AreEqual(expected, result);
        }

        [TestCase("Hallo Welt/Abc/Def", "hallo Welt/abc/Def")]
        [TestCase("Hallo Welt/Abc/Def", "Hallo Welt/abc/Def")]
        public void Test_NoLimitations_Path_NormalizeCase_NoReplacements(string expected, string text)
        {
            text = text.Replace('/', Path.DirectorySeparatorChar);
            expected = expected.Replace('/', Path.DirectorySeparatorChar);

            var limitations = new CharacterLimitations
            {
                NormalizeCase = true,
                Replacements = null,
                SupportedChars = null
            };
            var result = _sut.SanitizeText(limitations, text, true, out _);
            Assert.AreEqual(expected, result);
        }
    }
}