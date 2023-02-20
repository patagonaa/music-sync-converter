using MusicSyncConverter.FileProviders.SyncTargets.Adb;
using MusicSyncConverter.FileProviders.SyncTargets.Physical;
using MusicSyncConverter.FileProviders.SyncTargets.Wpd;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MusicSyncConverter.FileProviders.SyncTargets
{
    public class SyncTargetFactory
    {
        private static readonly char[] _pathSeperators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        public async Task<ISyncTarget> Get(string uriString, CancellationToken cancellationToken)
        {
            var splitUri = uriString.Split(':', 2);
            if (splitUri.Length < 2)
                throw new ArgumentException("Uri must contain protocol");

            switch (splitUri[0])
            {
                case "file":
                    {
                        var (path, sortMode) = ParseFileUri(uriString);
                        Directory.CreateDirectory(path);
                        return new PhysicalSyncTarget(path, sortMode);
                    }
                case "wpd":
                    {
                        if (!OperatingSystem.IsWindows())
                            throw new PlatformNotSupportedException("_Windows_ Portable Devices is not supported on non-Windows systems.");
                        var wpdPath = uriString.Replace("wpd://", "");
                        var pathParts = wpdPath.Split(_pathSeperators, 2);

                        return new WpdSyncTarget(pathParts[0], pathParts[1]);
                    }
                case "adb":
                    {
                        var adbPath = uriString.Replace("adb://", "");
                        var pathParts = adbPath.Split(_pathSeperators, 2);

                        return await AdbSyncTarget.Create(pathParts[0], pathParts[1], cancellationToken);
                    }
                default:
                    throw new ArgumentException($"Invalid URI Scheme: {splitUri[0]}");
            }
        }

        private static (string Path, FatSortMode FatSortMode) ParseFileUri(string uriString)
        {
            var pathQuerySplit = uriString.Replace("file://", "").Split('?');
            FatSortMode sortMode;
            if (pathQuerySplit.Length > 2)
            {
                throw new ArgumentException("Uri has more than one question mark. Use '%2F' to escape quastion marks within the path.");
            }
            else if (pathQuerySplit.Length == 2 && Enum.TryParse<FatSortMode>(HttpUtility.ParseQueryString(pathQuerySplit[1])["fatSortMode"], out var sortModeTmp))
            {
                sortMode = sortModeTmp;
            }
            else
            {
                sortMode = FatSortMode.None;
            }
            string path = HttpUtility.UrlDecode(pathQuerySplit[0]);

            return (path, sortMode);
        }
    }
}
