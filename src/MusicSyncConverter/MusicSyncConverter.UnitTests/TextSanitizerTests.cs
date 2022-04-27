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
                    new CharReplacement{ Char = 'ä', Replacement ="ae" },
                    new CharReplacement{ Char = 'ö', Replacement ="oe" },
                    new CharReplacement{ Char = 'ü', Replacement ="ue" },
                    new CharReplacement{ Char = 'Ä', Replacement ="Ae" },
                    new CharReplacement{ Char = 'Ö', Replacement ="Oe" },
                    new CharReplacement{ Char = 'Ü', Replacement ="Ue" },
                    new CharReplacement{ Char = 'ß', Replacement ="ss" },
                    new CharReplacement{ Char = '♥', Replacement ="<3" },
                    new CharReplacement{ Char = '×', Replacement ="x" }
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
                    new CharReplacement{ Char = 'ä', Replacement ="ae" },
                    new CharReplacement{ Char = 'ö', Replacement ="oe" },
                    new CharReplacement{ Char = 'ü', Replacement ="ue" },
                    new CharReplacement{ Char = 'Ä', Replacement ="Ae" },
                    new CharReplacement{ Char = 'Ö', Replacement ="Oe" },
                    new CharReplacement{ Char = 'Ü', Replacement ="Ue" },
                    new CharReplacement{ Char = 'ß', Replacement ="ss" },
                    new CharReplacement{ Char = 'ẞ', Replacement ="ss" },
                    new CharReplacement{ Char = '♥', Replacement ="<3" },
                    new CharReplacement{ Char = '×', Replacement ="x" }
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