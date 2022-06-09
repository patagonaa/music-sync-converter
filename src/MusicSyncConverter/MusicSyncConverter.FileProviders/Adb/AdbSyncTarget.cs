using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using MusicSyncConverter.FileProviders.Abstractions;
using SharpAdbClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.Adb
{
    internal class AdbSyncTarget : ISyncTarget, IDisposable
    {
        private readonly AdbClient _adbClient;
        private readonly DeviceData _device;
        private readonly SyncService _syncService;
        private readonly string _basePath;

        public AdbSyncTarget(string serial, string basePath)
        {
            _adbClient = new AdbClient();
            var devices = _adbClient.GetDevices();
            var device = devices.FirstOrDefault(x => x.Serial == serial);
            if (device == null)
                throw new ArgumentException($"Device {serial} not found! Available devices: {string.Join(";", devices.Select(x => x.Serial))}");
            _device = device;
            var ver = _adbClient.GetAdbVersion();
            _syncService = new SyncService(_adbClient, _device);
            _basePath = basePath;
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

            var receiver = new SyncTargetShellOutputReceiver();

            foreach (var batch in adbItems.Where(x => x.IsDirectory).Chunk(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string command = "rmdir " + string.Join(' ', batch.Select(x => EscapeFilename(x.FullPath)));
                await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, cancellationToken);
            }

            foreach (var batch in adbItems.Where(x => !x.IsDirectory).Chunk(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string command = "rm " + string.Join(' ', batch.Select(x => EscapeFilename(x.FullPath)));
                await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, cancellationToken);
            }

            if (receiver.Lines.Any())
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, receiver.Lines));
            }
        }

        private class SyncTargetShellOutputReceiver : IShellOutputReceiver
        {
            public bool ParsesErrors => false;

            public IList<string> Lines { get; private set; } = new List<string>();

            public void AddOutput(string line)
            {
                Lines.Add(line);
            }

            public void Flush()
            {
            }
        }

        private string EscapeFilename(string path)
        {
            return $"\"`echo {Convert.ToBase64String(AdbClient.Encoding.GetBytes(path))} | base64 -d`\"";
        }

        public void Dispose()
        {
            _syncService.Dispose();
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            lock (_syncService)
            {
                var stat = _syncService.Stat(path);
                if (stat.FileMode == 0)
                    return NotFoundDirectoryContents.Singleton;

                var dirList = _syncService.GetDirectoryListing(path);
                return new AdbDirectoryContents(path, dirList, _syncService);
            }
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            lock (_syncService)
            {
                var stat = _syncService.Stat(path);
                return new AdbFileInfo(path, stat, _syncService);
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

        public Task WriteFile(string subpath, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            var path = UnixizePath(Path.Join(_basePath, subpath));

            lock (_syncService)
            {
                _syncService.Push(content, path, 660, modified ?? DateTimeOffset.Now, null, cancellationToken);
            }
            return Task.CompletedTask;
        }

        internal static string UnixizePath(string path)
        {
            return path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }
    }
}
