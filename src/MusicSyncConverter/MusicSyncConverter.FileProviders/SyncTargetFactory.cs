using MusicSyncConverter.FileProviders.Abstractions;
using MusicSyncConverter.FileProviders.Adb;
using MusicSyncConverter.FileProviders.Physical;
using MusicSyncConverter.FileProviders.Wpd;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Web;

namespace MusicSyncConverter.FileProviders
{
    public class SyncTargetFactory
    {
        public async Task<ISyncTarget> Get(string uriString)
        {
            var splitUri = uriString.Split(':');
            if (splitUri.Length < 2)
                throw new ArgumentException("Uri must contain protocol");

            switch (splitUri[0])
            {
                case "file":
                    {
                        var pathQuerySplit = uriString.Replace("file://", "").Split('?');
                        var sortMode = (pathQuerySplit.Length == 2) && Enum.TryParse<FatSortMode>(HttpUtility.ParseQueryString(pathQuerySplit[1])["fatSortMode"], out var sortModeTmp)
                            ? sortModeTmp
                            : FatSortMode.None;
                        var path = pathQuerySplit[0];
                        Directory.CreateDirectory(path);
                        return new PhysicalSyncTarget(path, sortMode);
                    }
                case "wpd":
                    {
                        if (!OperatingSystem.IsWindows())
                            throw new PlatformNotSupportedException("_Windows_ Portable Devices is not supported on non-Windows systems.");
                        var wpdPath = uriString.Replace("wpd://", "");
                        var pathParts = wpdPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        return new WpdSyncTarget(pathParts[0], string.Join(Path.DirectorySeparatorChar, pathParts[1..]));
                    }
                case "adb":
                    {
                        var adbPath = uriString.Replace("adb://", "");
                        var pathParts = adbPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        return await AdbSyncTarget.Create(pathParts[0], string.Join(Path.DirectorySeparatorChar, pathParts[1..]));
                    }
                default:
                    throw new ArgumentException($"Invalid URI Scheme: {splitUri[0]}");
            }
        }
    }
}
