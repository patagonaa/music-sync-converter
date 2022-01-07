using System;
using System.IO;

namespace MusicSyncConverter
{
    internal static class TempFileHelper
    {
        public static string GetTempPath()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "MusicSync");
            Directory.CreateDirectory(tmpDir);
            return tmpDir;
        }

        public static string GetTempFilePath()
        {
            string tmpDir = GetTempPath();
            return Path.Combine(tmpDir, $"{Guid.NewGuid():D}.tmp");
        }
    }
}
