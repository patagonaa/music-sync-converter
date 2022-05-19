using NUnit.Framework;

namespace MusicSyncConverter.UnitTests
{
    [TestFixture]
    public class PathMatcherTests
    {
        private PathMatcher _sut;

        [SetUp]
        public void Setup()
        {
            _sut = new PathMatcher();
        }

        [TestCase(true, "Webradio", "Webradio", true)]
        [TestCase(false, "Web", "Webradio", true)]
        [TestCase(false, "radio", "Webradio", true)]
        [TestCase(false, "Webradio", "Web", true)]
        [TestCase(false, "Webradio", "radio", true)]
        [TestCase(true, "Music\\Artists", "Music\\Artists", true)]
        [TestCase(true, "Music/Artists", "Music/Artists", true)]
        [TestCase(true, "Music/Artists", "Music/aRtIsTs", false)]
        [TestCase(false, "Music/Artists", "Music/aRtIsTs", true)]
        [TestCase(true, "Music/aRtIsTs", "Music/Artists", false)]
        [TestCase(false, "Music/aRtIsTs", "Music/Artists", true)]
#if Windows
        [TestCase(true, "Music/Artists", "Music\\Artists", true)]
        [TestCase(true, "Music\\Artists", "Music/Artists", true)]
        [TestCase(true, "Music/aRtIsTs", "Music/Artists", false)]
        [TestCase(true, "Music/aRtIsTs", "Music\\Artists", false)]
        [TestCase(true, "Music/aRtIsTs", "Music/aRtIsTs", false)]
#endif
        public void EquivalentPathMatches(bool expected, string glob, string path, bool caseSensitive)
        {
            Assert.AreEqual(expected, _sut.Matches(glob, path, caseSensitive));
        }

        [TestCase(true, "Music/*/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/*/John Doe/Example Album", "Music//John Doe/Example Album")]
        [TestCase(true, "Music/*/*/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(false, "Music/*/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "*/Artists/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/Artists/John Doe/*", "Music/Artists/John Doe/Example Album")]
        public void SinglePathWildcardMatches(bool expected, string glob, string path)
        {
            Assert.AreEqual(expected, _sut.Matches(glob, path, true));
        }

        [TestCase(true, "Music/**/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/Artists/**/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/Artists/**", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "**/Example Album", "Music/Artists/John Doe/Example Album")]
        public void RecursivePathWildcardMatches(bool expected, string glob, string path)
        {
            Assert.AreEqual(expected, _sut.Matches(glob, path, true));
        }

        [TestCase(true, "Music/Artists/John Doe/Example*", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/Artists/John Doe/Example *", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "*usic/Artists/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "*Music/Artists/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/*Artists/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/Artists/John Doe/Example Album*", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/A*s/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(false, "Music/B*s/John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(false, "Music/Artists*/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(false, "Music/*John Doe/Example Album", "Music/Artists/John Doe/Example Album")]
        [TestCase(true, "Music/**/*.m3u", "Music/Artists/John Doe/Example Album/playlist.m3u")]
        [TestCase(false, "Music/**/*.m3u", "Music/Artists/John Doe/Example Album/playlist.mp3")]
        public void SinglePartialPathWildcardMatches(bool expected, string glob, string path)
        {
            Assert.AreEqual(expected, _sut.Matches(glob, path, true));
        }

    }
}
