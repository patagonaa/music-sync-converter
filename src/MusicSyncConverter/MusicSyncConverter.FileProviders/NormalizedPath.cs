namespace MusicSyncConverter.FileProviders
{
    public class NormalizedPath
    {
        public NormalizedPath(string path)
        {
            Path = PathUtils.NormalizePath(path);
        }

        public string Path { get; }

        public override string ToString()
        {
            return Path;
        }
    }
}
