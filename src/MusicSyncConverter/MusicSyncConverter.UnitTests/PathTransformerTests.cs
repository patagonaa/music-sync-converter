using Moq;
using MusicSyncConverter.Config;
using NUnit.Framework;
using System.IO;

namespace MusicSyncConverter.UnitTests
{
    [TestFixture]
    public class PathTransformerTests
    {
        private readonly PathTransformer _sut;

        public PathTransformerTests()
        {
            var sanitizerMock = new Mock<ITextSanitizer>(MockBehavior.Strict);
            bool hasUnsupported = false;
            sanitizerMock.Setup(x => x.SanitizePathPart(It.IsAny<CharacterLimitations?>(), It.IsAny<string>(), out hasUnsupported))
                .Returns<CharacterLimitations?, string, bool>((x, y, z) => y);

            _sut = new PathTransformer(new TextSanitizer());
        }

        [TestCase("Hallo Welt!", "Hallo Welt!")]
        [TestCase("Hallo Welt!", "hallo Welt!")]
        [TestCase("Ölle Testdaten", "ölle Testdaten")]
        [TestCase("Ölle Testdaten", "Ölle Testdaten")]
        [TestCase("Hallo Welt/Abc/Def", "hallo Welt/abc/Def")]
        [TestCase("Hallo Welt/Abc/Def", "Hallo Welt/abc/Def")]
        public void NormalizeCase(string expected, string text)
        {
            text = text.Replace('/', Path.DirectorySeparatorChar);
            expected = expected.Replace('/', Path.DirectorySeparatorChar);

            var deviceConfig = new TargetDeviceConfig
            {
                MaxDirectoryDepth = null,
                NormalizeCase = true
            };
            var result = _sut.TransformPath(text, PathTransformType.DirPath, deviceConfig, out _);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("", "")]
        [TestCase("Test", "Test")]
        [TestCase("One/Two/Three", "One/Two/Three")]
        [TestCase("One/Two/Three_Four", "One/Two/Three/Four")]
        public void DirPath_MaxDirDepth(string expected, string text)
        {
            text = text.Replace('/', Path.DirectorySeparatorChar);
            expected = expected.Replace('/', Path.DirectorySeparatorChar);

            var deviceConfig = new TargetDeviceConfig
            {
                MaxDirectoryDepth = 3,
                NormalizeCase = false
            };
            var result = _sut.TransformPath(text, PathTransformType.DirPath, deviceConfig, out _);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("", "")]
        [TestCase("Test.mp3", "Test.mp3")]
        [TestCase("One/Two/Three/Test.mp3", "One/Two/Three/Test.mp3")]
        [TestCase("One/Two/Three_Four/Test.mp3", "One/Two/Three/Four/Test.mp3")]
        [TestCase("One/Two/Four/Test.mp3", "One/Two/Three/../Four/Test.mp3")]
        public void FilePath_MaxDirDepth(string expected, string text)
        {
            text = text.Replace('/', Path.DirectorySeparatorChar);
            expected = expected.Replace('/', Path.DirectorySeparatorChar);

            var deviceConfig = new TargetDeviceConfig
            {
                MaxDirectoryDepth = 3,
                NormalizeCase = false
            };
            var result = _sut.TransformPath(text, PathTransformType.FilePath, deviceConfig, out _);
            Assert.That(result, Is.EqualTo(expected));
        }
    }
}
