using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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

        public static async Task<string> CopyToTempFile(Stream source, CancellationToken cancellationToken)
        {
            var filePath = GetTempFilePath();

            FileStream fileStream;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var fileHandle = new SafeFileHandle(CreateFile(filePath, FileAccess.ReadWrite, FileShare.None, IntPtr.Zero, FileMode.Create, FileAttributes.Temporary, IntPtr.Zero), true);
                fileStream = new FileStream(fileHandle, FileAccess.ReadWrite);
            }
            else
            {
                fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            }
            using (fileStream)
            {
                if (source.CanSeek)
                    fileStream.SetLength(source.Length);
                await source.CopyToAsync(fileStream, cancellationToken);
            }
            return filePath;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(
         [MarshalAs(UnmanagedType.LPTStr)] string filename,
         [MarshalAs(UnmanagedType.U4)] FileAccess access,
         [MarshalAs(UnmanagedType.U4)] FileShare share,
         IntPtr securityAttributes, // optional SECURITY_ATTRIBUTES struct or IntPtr.Zero
         [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
         [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
         IntPtr templateFile);
    }
}