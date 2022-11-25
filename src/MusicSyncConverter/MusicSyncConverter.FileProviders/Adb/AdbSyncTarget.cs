using AdbClient;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using MusicSyncConverter.FileProviders.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbSyncTarget : ISyncTarget, IDisposable
    {
        private static readonly Regex _adbTcpSerialRegex = new Regex(@"adb-(?<serial>[\w]+)-[\w]{6}\._adb-tls-connect\._tcp\.", RegexOptions.Compiled | RegexOptions.CultureInvariant); // https://github.com/aosp-mirror/platform_system_core/blob/34a0e57a257f0081c672c9be0e87230762e677ca/adb/daemon/mdns.cpp#L164
        private readonly string _deviceSerial;
        private readonly string _basePath;
        private readonly AdbServicesClient _adbClient;
        private readonly AdbSyncClient _syncService;

        private AdbSyncTarget(string deviceSerial, string basePath, AdbServicesClient adbClient, AdbSyncClient syncService)
        {
            _deviceSerial = deviceSerial;
            _basePath = basePath;
            _adbClient = adbClient;
            _syncService = syncService;
        }

        public static async Task<AdbSyncTarget> Create(string serial, string basePath)
        {
            try
            {
                await Process.Start("adb", "start-server").WaitForExitAsync();
            }
            catch (Exception)
            {
            }

            var adbClient = new AdbServicesClient();
            var devices = await adbClient.GetDevices();
            var device = devices.FirstOrDefault(x => IsRequestedDevice(x, serial));
            if (device == default)
                throw new ArgumentException($"Device {serial} not found! Available devices: {string.Join(";", devices.Select(x => x.Serial))}");
            var syncClient = await adbClient.GetSyncClient(device.Serial);

            return new AdbSyncTarget(device.Serial, basePath, adbClient, syncClient);
        }

        private static bool IsRequestedDevice((string Serial, string _) device, string serial)
        {
            if (device.Serial == serial)
                return true;
            var match = _adbTcpSerialRegex.Match(device.Serial);
            if (match.Success && match.Groups["serial"].Value == serial)
                return true;
            return false;
        }

        public Task Complete(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task Delete(IFileInfo file, CancellationToken cancellationToken)
        {
            await Delete(new[] { file }, cancellationToken);
        }

        public async Task Delete(IReadOnlyCollection<IFileInfo> files, CancellationToken cancellationToken)
        {
            var adbItems = files.OfType<AdbFileInfo>().ToList();
            if (adbItems.Count != files.Count)
            {
                throw new ArgumentException("all files must be AdbFileInfo", nameof(files));
            }

            foreach (var batch in adbItems.Where(x => x.IsDirectory).Chunk(10))
            {
                using (var ms = new MemoryStream())
                {
                    var returnCode = await _adbClient.Execute(_deviceSerial, "rmdir", batch.Select(x => x.FullPath), null, ms, ms, cancellationToken);
                    if (returnCode != 0)
                    {
                        throw new Exception(Encoding.UTF8.GetString(ms.ToArray()));
                    }
                }
            }

            foreach (var batch in adbItems.Where(x => !x.IsDirectory).Chunk(10))
            {
                using (var ms = new MemoryStream())
                {
                    var returnCode = await _adbClient.Execute(_deviceSerial, "rm", batch.Select(x => x.FullPath), null, ms, ms, cancellationToken);
                    if (returnCode != 0)
                    {
                        throw new Exception(Encoding.UTF8.GetString(ms.ToArray()));
                    }
                }
            }
        }

        public void Dispose()
        {
            _syncService.Dispose();
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            var stat = _syncService.Stat(path).Result;
            if (stat.Mode == 0)
                return NotFoundDirectoryContents.Singleton;

            var dirList = _syncService.List(path).Result;
            return new AdbDirectoryContents(path, dirList, _syncService);
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            var stat = _syncService.Stat(path).Result;
            return new AdbFileInfo(path, stat, _syncService);
        }

        public bool IsCaseSensitive()
        {
            return true;
        }

        public IChangeToken Watch(string filter)
        {
            throw new NotImplementedException();
        }

        public async Task WriteFile(string subpath, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));
            await _syncService.Push(path, (UnixFileMode)Convert.ToInt32("660", 8), modified ?? DateTimeOffset.Now, content, cancellationToken);
            var fileUrl = $"file://{string.Join('/', path.Split('/').Select(x => Uri.EscapeDataString(x)))}";
            var command = "am";
            var parms = new string[] { "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE", "-d", fileUrl };
            using (var ms = new MemoryStream())
            {
                var returnCode = await _adbClient.Execute(_deviceSerial, command, parms, null, ms, ms, cancellationToken);
                if (returnCode != 0)
                {
                    throw new Exception(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
        }

        internal static string UnixizePath(string path)
        {
            return path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }
    }
}
