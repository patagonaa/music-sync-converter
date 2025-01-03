﻿using MusicSyncConverter.FileProviders;
using NUnit.Framework;

namespace MusicSyncConverter.UnitTests
{
    [TestFixture]
    public class PathComparerTests
    {
        [TestCase(true, "", "")]
        [TestCase(true, "/", "/")]
        [TestCase(true, "test", "test")]
        [TestCase(true, "test", "tEsT")]
        [TestCase(true, "test/file", "test/file")]
        [TestCase(true, "test/file", "test\\file")]
        [TestCase(true, "test/file", "tEsT\\fIlE")]
        [TestCase(true, "test/../file", "file")]
        [TestCase(true, "test/../file", "fIlE")]
        [TestCase(true, "test/file/..", "test")]
        [TestCase(true, "test/file/..", "tEsT")]
        [TestCase(true, "./test", "test")]
        [TestCase(true, "./test", "tEsT")]
        [TestCase(true, "test/./file", "test/file")]
        [TestCase(true, "test/./file", "tEsT/fIlE")]
        [TestCase(false, "/test/file", "test/file")]
        [TestCase(false, "test", null)]
        [TestCase(true, null, null)]
        public void CaseInsensitive_Equals(bool expected, string path1, string path2)
        {
            var sut = new PathComparer(false);
            Assert.That(sut.Equals(path1, path2), Is.EqualTo(expected));
            Assert.That(sut.Equals(path2, path1), Is.EqualTo(expected));
        }

        [TestCase(true, "", "")]
        [TestCase(true, "/", "/")]
        [TestCase(true, "test", "test")]
        [TestCase(false, "test", "tEsT")]
        [TestCase(true, "test/file", "test/file")]
        [TestCase(true, "test/file", "test\\file")]
        [TestCase(false, "test/file", "tEsT\\fIlE")]
        [TestCase(true, "test/../file", "file")]
        [TestCase(false, "test/../file", "fIlE")]
        [TestCase(true, "test/file/..", "test")]
        [TestCase(false, "test/file/..", "tEsT")]
        [TestCase(true, "./test", "test")]
        [TestCase(false, "./test", "tEsT")]
        [TestCase(true, "test/./file", "test/file")]
        [TestCase(false, "test/./file", "tEsT/fIlE")]
        [TestCase(false, "/test/file", "test/file")]
        [TestCase(false, "test", null)]
        [TestCase(true, null, null)]
        public void CaseSensitive_Equals(bool expected, string path1, string path2)
        {
            var sut = new PathComparer(true);
            Assert.That(sut.Equals(path1, path2), Is.EqualTo(expected));
            Assert.That(sut.Equals(path2, path1), Is.EqualTo(expected));
        }
    }
}
