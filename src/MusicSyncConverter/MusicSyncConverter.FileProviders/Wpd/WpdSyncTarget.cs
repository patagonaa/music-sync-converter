using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using MusicSyncConverter.FileProviders.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static Vanara.PInvoke.Ole32;
using static Vanara.PInvoke.PortableDeviceApi;

namespace MusicSyncConverter.FileProviders.Wpd
{
    // https://docs.microsoft.com/en-us/windows/win32/windows-portable-devices
    // https://github.com/teapottiger/WPDApi/blob/62be7c4acc6104aef108769b4496b35b0a7fbd53/PortableDevices/PortableDevice.cs
    // https://github.com/dahall/Vanara/blob/76722fbcf5c1f90dccee9751dc0367641e37b4f9/UnitTests/PInvoke/PortableDeviceApi/PortableDeviceApiTests.cs

    public class WpdSyncTarget : ISyncTarget, IDisposable
    {
        private readonly object _syncLock = new object();
        private readonly IPortableDevice _device;
        private readonly IPortableDeviceContent _content;
        private readonly IPortableDeviceProperties _contentProperties;
        private readonly string _basePath;
        private readonly PathComparer _pathComparer;
        private readonly Dictionary<string, IDirectoryContents> _directoryContentsCache;
        private readonly Dictionary<string, string> _objectIdCache;

        public WpdSyncTarget(string friendlyName, string basePath)
        {
            _device = GetDevice(friendlyName);
            _content = _device.Content();
            _contentProperties = _content.Properties();
            _basePath = basePath;
            _pathComparer = new PathComparer(IsCaseSensitive());
            _directoryContentsCache = new Dictionary<string, IDirectoryContents>(_pathComparer);
            _objectIdCache = new Dictionary<string, string>(_pathComparer);
        }

        private IPortableDevice GetDevice(string toFind)
        {
            var deviceMan = new IPortableDeviceManager();

            var deviceNames = new List<string>();
            foreach (var deviceId in deviceMan.GetDevices())
            {
                string friendlyName = deviceMan.GetDeviceFriendlyName(deviceId);
                deviceNames.Add(friendlyName);
                if (friendlyName == toFind)
                {
                    var device = new IPortableDevice();
                    device.Open(deviceId, GetClientInfo());
                    return device;
                }
            }
            throw new InvalidOperationException($"Device {toFind} not found! Available devices: {string.Join(";", deviceNames)}");
        }

        private IPortableDeviceValues GetClientInfo()
        {
            var clientInfo = new IPortableDeviceValues();
            clientInfo.SetStringValue(WPD_CLIENT_NAME, "MusicSyncConverter");
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_MAJOR_VERSION, 1);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_MINOR_VERSION, 0);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_REVISION, 0);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_DESIRED_ACCESS, ACCESS_MASK.GENERIC_READ | ACCESS_MASK.GENERIC_WRITE);
            return clientInfo;
        }

        public bool IsCaseSensitive()
        {
            return false; // TODO do we even care?
        }

        public Task WriteFile(string path, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            lock (_syncLock)
            {
                var sw = Stopwatch.StartNew();
                var directoryObjectId = CreateDirectoryStructure(Path.GetDirectoryName(path) ?? throw new ArgumentNullException(nameof(path)));
                var fileName = Path.GetFileName(path);
                Debug.WriteLine("CreateDir " + sw.ElapsedMilliseconds+"ms");
                sw.Restart();

                var existingFileObjId = GetObjectId(fileName, directoryObjectId);
                if (existingFileObjId != null)
                {
                    var objsToDelete = new IPortableDevicePropVariantCollection();
                    objsToDelete.Add(new PROPVARIANT(existingFileObjId));
                    _content.Delete(DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_NO_RECURSION, objsToDelete);
                }

                Debug.WriteLine("DeleteExisting " + sw.ElapsedMilliseconds+"ms");
                sw.Restart();

                var properties = new IPortableDeviceValues();
                properties.SetStringValue(WPD_OBJECT_PARENT_ID, directoryObjectId);
                properties.SetUnsignedLargeIntegerValue(WPD_OBJECT_SIZE, checked((ulong)content.Length));
                properties.SetGuidValue(WPD_OBJECT_CONTENT_TYPE, WPD_CONTENT_TYPE_GENERIC_FILE);
                properties.SetStringValue(WPD_OBJECT_ORIGINAL_FILE_NAME, fileName);
                properties.SetStringValue(WPD_OBJECT_NAME, fileName);
                if (modified.HasValue)
                    properties.SetValue(WPD_OBJECT_DATE_MODIFIED, new PROPVARIANT(modified.Value.LocalDateTime));

                uint bufferSize = 0;
                _content.CreateObjectWithPropertiesAndData(properties, out IStream stream, ref bufferSize);

                try
                {
                    var buffer = new byte[bufferSize];
                    int bytesRead;
                    while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Write(buffer, bytesRead, IntPtr.Zero);
                    }
                    stream.Commit(0);
                }
                finally
                {
                    Marshal.ReleaseComObject(stream);
                }
                sw.Stop();
                Debug.WriteLine("Write " + sw.ElapsedMilliseconds + "ms (" + (content.Length / sw.Elapsed.TotalSeconds / 1024 / 1024).ToString("F2") + "MiB/s)");

                ClearCaches();
            }
            return Task.CompletedTask;
        }

        private string CreateDirectoryStructure(string subPath)
        {
            var pathParts = Path.Join(_basePath, subPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var keys = new IPortableDeviceKeyCollection();
            keys.Add(WPD_OBJECT_NAME);

            var currentObject = WPD_DEVICE_OBJECT_ID;
            foreach (var pathPart in pathParts)
            {
                var items = _content.EnumObjects(0, currentObject);
                var foundChild = false;
                foreach (var item in items.Enumerate())
                {
                    var itemProperties = _contentProperties.GetValues(item, keys);
                    if (_pathComparer.FileNameEquals(itemProperties?.GetStringValue(WPD_OBJECT_NAME), pathPart))
                    {
                        foundChild = true;
                        currentObject = item;
                        break;
                    }
                }
                if (!foundChild)
                {
                    currentObject = CreateDirectory(currentObject, pathPart);
                }
            }
            return currentObject;
        }

        private string CreateDirectory(string parentObject, string name)
        {
            var properties = new IPortableDeviceValues();
            properties.SetGuidValue(WPD_OBJECT_CONTENT_TYPE, WPD_CONTENT_TYPE_FOLDER);
            properties.SetStringValue(WPD_OBJECT_PARENT_ID, parentObject);
            properties.SetStringValue(WPD_OBJECT_ORIGINAL_FILE_NAME, name);
            properties.SetStringValue(WPD_OBJECT_NAME, name);

            return _content.CreateObjectWithPropertiesOnly(properties);
        }

        public IFileInfo? GetFileInfo(string subPath)
        {
            lock (_syncLock)
            {
                var obj = GetObjectId(subPath);
                if (obj == null)
                    return new NotFoundFileInfo(subPath);

                var fileInfo = new WpdFileInfo(obj, _syncLock, _content, _contentProperties);

                // this part is really stupid but at least PhysicalFileProvider returns NotFound in this case,
                // so we do the same here so callers don't rely on GetFileInfo working for directories.
                if (fileInfo.IsDirectory)
                    return new NotFoundFileInfo(subPath);

                return fileInfo;
            }
        }

        public IDirectoryContents GetDirectoryContents(string subPath)
        {
            lock (_syncLock)
            {
                if (!_directoryContentsCache.TryGetValue(subPath, out IDirectoryContents? directoryContents))
                {
                    var obj = GetObjectId(subPath);
                    directoryContents = obj == null ? NotFoundDirectoryContents.Singleton : new WpdDirectoryContents(obj, _syncLock, _content, _contentProperties);
                    _directoryContentsCache.TryAdd(subPath, directoryContents);
                }
                return directoryContents;
            }
        }

        private string? GetObjectId(string objPath)
        {
            var absolutePath = Path.Join(_basePath, objPath);
            return GetObjectId(absolutePath, WPD_DEVICE_OBJECT_ID);

        }
        private string? GetObjectId(string objPath, string searchRootObjId)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var pathParts = objPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var keys = new IPortableDeviceKeyCollection();
                keys.Add(WPD_OBJECT_NAME);

                var currentObject = searchRootObjId;
                var currentPath = string.Empty;
                for (int i = 0; i < pathParts.Length; i++)
                {
                    string? pathPart = pathParts[i];
                    currentPath = Path.Join(currentPath, pathPart);

                    if (_objectIdCache.TryGetValue(currentPath, out var foundObjId))
                    {
                        currentObject = foundObjId;
                    }
                    else
                    {
                        var items = _content.EnumObjects(0, currentObject);
                        var foundChild = false;
                        foreach (var item in items.Enumerate())
                        {
                            var itemProperties = _contentProperties.GetValues(item, keys);
                            if (_pathComparer.FileNameEquals(itemProperties?.GetStringValue(WPD_OBJECT_NAME), pathPart))
                            {
                                foundChild = true;
                                currentObject = item;
                                break;
                            }
                        }
                        if (foundChild)
                        {
                            _objectIdCache[currentPath] = currentObject;
                            continue;
                        }
                        return null;
                    }
                }
                return currentObject;
            }
            finally
            {
                sw.Stop();
                Debug.WriteLine("GetObjectId {0}ms", sw.ElapsedMilliseconds);
            }
        }

        public IChangeToken Watch(string filter)
        {
            throw new NotImplementedException();
        }

        public Task Delete(IReadOnlyCollection<IFileInfo> files, CancellationToken cancellationToken)
        {
            var wpdFiles = files.OfType<WpdFileInfo>().ToList();
            if (wpdFiles.Count != files.Count)
            {
                throw new ArgumentException("all files must be WpdFileInfo", nameof(files));
            }

            lock (_syncLock)
            {
                var sw = Stopwatch.StartNew();
                var objsToDelete = new IPortableDevicePropVariantCollection();
                foreach (var wpdFile in wpdFiles)
                {
                    objsToDelete.Add(new PROPVARIANT(wpdFile.ObjectId));
                }
                _content.Delete(DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_NO_RECURSION, objsToDelete);
                ClearCaches();
                sw.Stop();
                Debug.WriteLine("Delete {0} in {1}ms", wpdFiles.Count, sw.ElapsedMilliseconds);
            }
            return Task.CompletedTask;
        }

        public Task Delete(IFileInfo file, CancellationToken cancellationToken)
        {
            return Delete(new[] { file }, cancellationToken);
        }

        private void ClearCaches()
        {
            _directoryContentsCache.Clear();
            _objectIdCache.Clear();
        }

        public Task Complete(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                _device.Close();
            }
        }
    }
}
