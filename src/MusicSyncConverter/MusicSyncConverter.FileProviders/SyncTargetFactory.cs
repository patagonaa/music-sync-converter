using MusicSyncConverter.FileProviders.Abstractions;
using MusicSyncConverter.FileProviders.Physical;
using MusicSyncConverter.FileProviders.Wpd;
using System;
using System.IO;

namespace MusicSyncConverter.FileProviders
{
    public class SyncTargetFactory
    {
        public ISyncTarget Get(string uriString)
        {
            var splitUri = uriString.Split(':');
            if (splitUri.Length < 2)
                throw new ArgumentException("Uri must contain protocol");

            switch (splitUri[0])
            {
                case "file":
                    {
                        return new PhysicalSyncTarget(uriString.Replace("file://", ""));
                    }
                case "wpd":
                    {
                        var wpdPath = uriString.Replace("wpd://", "");
                        var pathParts = wpdPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        return new WpdSyncTarget(pathParts[0], string.Join(Path.DirectorySeparatorChar, pathParts[1..]));
                    }
                default:
                    throw new ArgumentException($"Invalid URI Scheme: {splitUri[0]}");
            }
        }
    }
}
