using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    public class TempFileService
    {
        private readonly string _tempPath;

        public TempFileService()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), "MusicSync");
        }

        public void CleanupTempDir(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_tempPath))
            {
                return;
            }
            var thresholdDate = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var file in Directory.GetDirectories(_tempPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new DirectoryInfo(file);
                if (fileInfo.LastWriteTimeUtc < thresholdDate)
                    fileInfo.Delete(true);
            }
        }

        public ITempFileSession GetNewSession()
        {
            var sessionPath = Path.Combine(_tempPath, Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(sessionPath);
            return new TempFileSession(sessionPath);
        }

        private class TempFileSession : ITempFileSession
        {
            private readonly string _sessionPath;

            internal TempFileSession(string sessionPath)
            {
                _sessionPath = sessionPath;
            }

            public string GetTempFilePath(string extension = ".tmp")
            {
                return Path.Combine(_sessionPath, Path.ChangeExtension(Guid.NewGuid().ToString("D"), extension));
            }

            public async Task<(string path, long length)> CopyToTempFile(Stream source, string extension = ".tmp", CancellationToken cancellationToken = default)
            {
                var filePath = GetTempFilePath(extension);

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
                long length;
                using (fileStream)
                {
                    if (source.CanSeek)
                        fileStream.SetLength(source.Length);
                    await source.CopyToAsync(fileStream, cancellationToken);
                    length = fileStream.Length;
                }
                return (filePath, length);
            }

            public void Dispose()
            {
                using var timeoutCts = new CancellationTokenSource(10000);

                while (!timeoutCts.IsCancellationRequested)
                {
                    try
                    {
                        Directory.Delete(_sessionPath, true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }
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

    public interface ITempFileSession : IDisposable
    {
        Task<(string path, long length)> CopyToTempFile(Stream source, string extension = ".tmp", CancellationToken cancellationToken = default);
        string GetTempFilePath(string extension = ".tmp");
    }
}