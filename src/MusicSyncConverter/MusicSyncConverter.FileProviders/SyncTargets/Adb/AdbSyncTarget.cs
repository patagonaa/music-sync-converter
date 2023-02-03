using AdbClient;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.SyncTargets.Adb
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

        public static async Task<AdbSyncTarget> Create(string serial, string basePath, CancellationToken cancellationToken)
        {
            try
            {
                await Process.Start("adb", "start-server").WaitForExitAsync(cancellationToken);
            }
            catch (Exception)
            {
            }

            var adbClient = new AdbServicesClient();
            var devices = await adbClient.GetDevices(cancellationToken);
            var actualSerial = devices.FirstOrDefault(x => IsRequestedDevice(x, serial) && x.State == "device").Serial;
            if (actualSerial == null)
            {
                Console.WriteLine($"Device {serial} not found!" + (devices.Count > 0 ? $" Available devices: {string.Join(";", devices.Select(x => x.Serial))}" : string.Empty));
                Console.WriteLine("Waiting for device...");

                var trackDevicesCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await foreach (var deviceStatus in adbClient.TrackDevices(cancellationToken))
                {
                    Console.WriteLine(deviceStatus.ToString());
                    if (IsRequestedDevice(deviceStatus, serial) && deviceStatus.State == "device")
                    {
                        actualSerial = deviceStatus.Serial;
                        trackDevicesCts.Cancel();
                        break;
                    }
                }
                Debug.Assert(actualSerial != null);

                Console.WriteLine("Found device!");
            }
            var syncClient = await adbClient.GetSyncClient(actualSerial, cancellationToken);

            return new AdbSyncTarget(actualSerial, basePath, adbClient, syncClient);
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

        public async Task Delete(IReadOnlyCollection<SyncTargetFileInfo> adbItems, CancellationToken cancellationToken)
        {
            foreach (var batch in adbItems.Where(x => x.IsDirectory).Chunk(10))
            {
                using (var ms = new MemoryStream())
                {
                    var returnCode = await _adbClient.Execute(_deviceSerial, "rmdir", batch.Select(x => GetUnixPath(x.Path!)), null, ms, ms, cancellationToken);
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
                    var returnCode = await _adbClient.Execute(_deviceSerial, "rm", batch.Select(x => GetUnixPath(x.Path!)), null, ms, ms, cancellationToken);
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

        public async Task<IList<SyncTargetFileInfo>?> GetDirectoryContents(string subpath, CancellationToken cancellationToken = default)
        {
            var path = GetUnixPath(subpath);

            var stat = await _syncService.Stat(path, cancellationToken);
            if (stat.Mode == 0)
                return null;

            var dirList = await _syncService.List(path, cancellationToken);
            return dirList.Select(x => MapToFileInfo(x, Path.Join(path, x.Path))).ToList();
        }

        public async Task<SyncTargetFileInfo?> GetFileInfo(string subpath, CancellationToken cancellationToken = default)
        {
            var path = GetUnixPath(subpath);

            var stat = await _syncService.Stat(path, cancellationToken);
            if (stat.Mode == 0)
                return null;
            return MapToFileInfo(stat, subpath);
        }

        private SyncTargetFileInfo MapToFileInfo(StatEntry stat, string fullPath)
        {
            return new SyncTargetFileInfo(fullPath, stat.Path, stat.Mode.HasFlag(UnixFileMode.Directory), stat.ModifiedTime);
        }

        public Task<bool> IsCaseSensitive()
        {
            return Task.FromResult(true);
        }

        public Task<bool> IsHidden(string path, bool recurse)
        {
            var pathParts = PathUtils.GetPathStack(path);
            return Task.FromResult(recurse ? pathParts.Any(x => x.StartsWith('.')) : pathParts.First().StartsWith('.'));
        }

        public IChangeToken Watch(string filter)
        {
            throw new NotImplementedException();
        }

        public async Task WriteFile(string subpath, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            var path = GetUnixPath(subpath);
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

        private string GetUnixPath(string subpath)
        {
            return PathUtils.MakeUnixPath(Path.Join(_basePath, subpath));
        }
    }
}
