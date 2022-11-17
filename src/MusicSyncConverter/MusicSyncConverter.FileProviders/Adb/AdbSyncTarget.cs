using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using MusicSyncConverter.AdbAbstraction;
using MusicSyncConverter.FileProviders.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbSyncTarget : ISyncTarget, IDisposable
    {
        private readonly AdbClient _adbClient;
        private readonly string _deviceSerial;
        private readonly AdbSyncClient _syncService;
        private readonly string _basePath;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public AdbSyncTarget(string serial, string basePath)
        {
            _adbClient = new AdbClient();
            var devices = _adbClient.GetDevices().Result;
            var device = devices.FirstOrDefault(x => x.Serial == serial);
            if (device == default)
                throw new ArgumentException($"Device {serial} not found! Available devices: {string.Join(";", devices.Select(x => x.Serial))}");
            _deviceSerial = device.Serial;
            _syncService = _adbClient.GetSyncClient(serial).Result;
            _basePath = basePath;
        }

        private static readonly Regex _adbTcpSerialRegex = new Regex(@"adb-(?<serial>[\w]+)-[\w]{6}\._adb-tls-connect\._tcp\.", RegexOptions.Compiled | RegexOptions.CultureInvariant); // https://github.com/aosp-mirror/platform_system_core/blob/34a0e57a257f0081c672c9be0e87230762e677ca/adb/daemon/mdns.cpp#L164

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

            //var receiver = new SyncTargetShellOutputReceiver();

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                foreach (var batch in adbItems.Where(x => x.IsDirectory).Chunk(10))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    //string command = "rmdir " + string.Join(' ', batch.Select(x => EscapeFilename(x.FullPath)));
                    //await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, cancellationToken);
                }

                foreach (var batch in adbItems.Where(x => !x.IsDirectory).Chunk(10))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    //string command = "rm " + string.Join(' ', batch.Select(x => EscapeFilename(x.FullPath)));
                    //await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, cancellationToken);
                }
            }
            finally
            {
                _semaphore.Release();
            }

            //if (receiver.Lines.Any())
            //{
            //    throw new InvalidOperationException(string.Join(Environment.NewLine, receiver.Lines));
            //}
        }

        private string EscapeFilename(string path)
        {
            return $"\"`echo {Convert.ToBase64String(AdbSyncClient.PathEncoding.GetBytes(path))} | base64 -d`\"";
        }

        public void Dispose()
        {
            _semaphore.Wait();
            _syncService.Dispose();
            _semaphore.Dispose();
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            _semaphore.Wait();
            try
            {
                var stat = _syncService.Stat(path).Result;
                if (stat.Mode == 0)
                    return NotFoundDirectoryContents.Singleton;

                var dirList = _syncService.List(path).Result;
                return new AdbDirectoryContents(path, dirList, _syncService, _semaphore);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            _semaphore.Wait();
            try
            {
                var stat = _syncService.Stat(path).Result;
                return new AdbFileInfo(path, stat, _syncService, _semaphore);
            }
            finally
            {
                _semaphore.Release();
            }
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

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await _syncService.Push(path, (UnixFileMode)Convert.ToInt32("660", 8), modified ?? DateTimeOffset.Now, content, cancellationToken);
                //var receiver = new SyncTargetShellOutputReceiver();
                //var fileUrl = $"file://{string.Join('/', path.Split('/').Select(x => Uri.EscapeDataString(x)))}";
                //var command = $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d '{fileUrl}'";
                //await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, cancellationToken);
                //Debug.WriteLine(receiver.ToString());
            }
            finally
            {
                _semaphore.Release();
            }
        }

        internal static string UnixizePath(string path)
        {
            return path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        //private class SyncTargetShellOutputReceiver : IShellOutputReceiver
        //{
        //    public bool ParsesErrors => false;

        //    public IList<string> Lines { get; private set; } = new List<string>();

        //    public void AddOutput(string line)
        //    {
        //        Lines.Add(line);
        //    }

        //    public void Flush()
        //    {
        //    }

        //    public override string ToString()
        //    {
        //        return string.Join(Environment.NewLine, Lines);
        //    }
        //}
    }
}
