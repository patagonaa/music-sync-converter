using System;
using System.IO;

namespace MusicSyncConverter
{
    internal static class TempFileHelper
    {
        public static string GetTempFilePath()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "MusicSync");
            Directory.CreateDirectory(tmpDir);
            return Path.Combine(tmpDir, $"{Guid.NewGuid():D}.tmp");
        }
    }
}
