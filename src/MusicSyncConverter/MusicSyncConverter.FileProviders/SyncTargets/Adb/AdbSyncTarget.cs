﻿using AdbClient;
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
        private static readonly Regex _adbTcpSerialRegex = new Regex(@"adb-(?<serial>[\w]+)-[\w]{6}\._adb-tls-connect\._tcp\.?", RegexOptions.Compiled | RegexOptions.CultureInvariant); // https://github.com/aosp-mirror/platform_system_core/blob/34a0e57a257f0081c672c9be0e87230762e677ca/adb/daemon/mdns.cpp#L164
        private readonly string _deviceSerial;
        private readonly string _basePath;
        private readonly AdbServicesClient _adbClient;
        private readonly AdbSyncClient _syncService;

        private readonly SemaphoreSlim _caseCheckSemaphore = new(1, 1);
        private bool? _isCaseSensitive = null;

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
                var startInfo = new ProcessStartInfo("adb", "start-server");

                // enable adb's internal MDNS handler to allow MDNS connections without
                // separate MDNS service
                startInfo.EnvironmentVariables["ADB_MDNS_OPENSCREEN"] = "1";

                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("adb could not be started");
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not start ADB server: " + ex);
            }

            var adbClient = new AdbServicesClient();
            var devices = await adbClient.GetDevices(cancellationToken);
            var actualSerial = devices.FirstOrDefault(x => IsRequestedDevice(x, serial) && x.State == AdbConnectionState.Device).Serial;
            if (actualSerial == null)
            {
                Console.WriteLine($"Device {serial} not found!" + (devices.Count > 0 ? $" Available devices: {string.Join(";", devices.Select(x => x.Serial))}" : string.Empty));
                Console.WriteLine("Waiting for device...");

                await foreach (var deviceStatus in adbClient.TrackDevices(cancellationToken))
                {
                    Console.WriteLine(deviceStatus.ToString());
                    if (IsRequestedDevice(deviceStatus, serial) && deviceStatus.State == AdbConnectionState.Device)
                    {
                        actualSerial = deviceStatus.Serial;
                        break;
                    }
                }
                Debug.Assert(actualSerial != null);

                Console.WriteLine("Found device!");
            }
            var syncClient = await adbClient.GetSyncClient(actualSerial, cancellationToken);

            return new AdbSyncTarget(actualSerial, basePath, adbClient, syncClient);
        }

        private static bool IsRequestedDevice((string Serial, AdbConnectionState _) device, string serial)
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
            foreach (var batch in adbItems.Where(x => !x.IsDirectory).Chunk(10))
            {
                using (var ms = new MemoryStream())
                {
                    var returnCode = await _adbClient.Execute(_deviceSerial, "rm", batch.Select(x => GetUnixPath(x.Path)), null, ms, ms, cancellationToken);
                    if (returnCode != 0)
                    {
                        throw new Exception(Encoding.UTF8.GetString(ms.ToArray()));
                    }
                }
            }

            foreach (var batch in adbItems.Where(x => x.IsDirectory).Chunk(10))
            {
                using (var ms = new MemoryStream())
                {
                    var returnCode = await _adbClient.Execute(_deviceSerial, "rmdir", batch.Select(x => GetUnixPath(x.Path)), null, ms, ms, cancellationToken);
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

            var dirStat = await _syncService.StatV2(path, cancellationToken: cancellationToken);

            return dirStat.Error switch
            {
                AdbSyncErrorCode.None => (await _syncService.ListV2(path, cancellationToken))
                    .Select(x => MapToFileInfo(x, Path.Join(subpath, GetFileName(x.FullPath))))
                    .ToList(),
                AdbSyncErrorCode.ENOENT => null,
                _ => throw new Exception($"Stat {path}: {dirStat.Error}"),
            };
        }

        private static string GetFileName(string path)
        {
            return path.Split('/').Last();
        }

        public async Task<SyncTargetFileInfo?> GetFileInfo(string subpath, CancellationToken cancellationToken = default)
        {
            var path = GetUnixPath(subpath);

            var stat = await _syncService.StatV2(path, cancellationToken: cancellationToken);
            return stat.Error switch
            {
                AdbSyncErrorCode.None => MapToFileInfo(stat, subpath),
                AdbSyncErrorCode.ENOENT => null,
                _ => throw new Exception($"Stat {path}: {stat.Error}"),
            };
        }

        private static SyncTargetFileInfo MapToFileInfo(StatV2Entry stat, string path)
        {
            return new SyncTargetFileInfo(path, stat.Mode.HasFlag(AdbClient.UnixFileMode.Directory), stat.ModifiedTime);
        }

        public async Task<bool> IsCaseSensitive(CancellationToken cancellationToken = default)
        {
            if (_isCaseSensitive == null)
            {
                await _caseCheckSemaphore.WaitAsync(cancellationToken);
                try
                {
                    if(_isCaseSensitive == null)
                        _isCaseSensitive = await IsCaseSensitiveInternal(cancellationToken);
                }
                finally
                {
                    _caseCheckSemaphore.Release();
                }
            }
            return _isCaseSensitive.Value;
        }

        public async Task<bool> IsCaseSensitiveInternal(CancellationToken token)
        {
            var path1 = "test.tmp";
            var path2 = "TEST.tmp";

            var f1 = await GetFileInfo(path1, token);
            if (f1 != null)
                await Delete([f1], token);

            var f2 = await GetFileInfo(path2, token);
            if (f2 != null)
                await Delete([f2], token);

            using var emptyStream = new MemoryStream();
            await WriteFile(path1, emptyStream, cancellationToken: token);

            var toReturn = (await GetFileInfo(path2, token)) == null;

            await Delete([(await GetFileInfo(path1, token))!], token);
            return toReturn;
        }

        public Task<bool> IsHidden(string path)
        {
            var pathParts = PathUtils.GetPathStack(path);
            return Task.FromResult(pathParts.First().StartsWith('.'));
        }

        public IChangeToken Watch(string filter)
        {
            throw new NotImplementedException();
        }

        public async Task WriteFile(string subpath, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            var path = GetUnixPath(subpath);
            await _syncService.Push(path, content, modified ?? DateTimeOffset.Now, (AdbClient.UnixFileMode)Convert.ToInt32("660", 8), cancellationToken);
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
