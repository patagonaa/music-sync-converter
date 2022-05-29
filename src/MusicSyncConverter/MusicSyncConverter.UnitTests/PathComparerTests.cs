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
        [TestCase(true, "test/./file", "test/file")]
        [TestCase(true, "test/./file", "tEsT/fIlE")]
        [TestCase(false, "/test/file", "test/file")]
        public void CaseInsensitive(bool expected, string path1, string path2)
        {
            var sut = new PathComparer(false);
            Assert.AreEqual(expected, sut.Equals(path1, path2));
            Assert.AreEqual(expected, sut.Equals(path2, path1));
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
        [TestCase(true, "test/./file", "test/file")]
        [TestCase(false, "test/./file", "tEsT/fIlE")]
        [TestCase(false, "/test/file", "test/file")]
        public void CaseSensitive(bool expected, string path1, string path2)
        {
            var sut = new PathComparer(true);
            Assert.AreEqual(expected, sut.Equals(path1, path2));
            Assert.AreEqual(expected, sut.Equals(path2, path1));
        }
    }
}