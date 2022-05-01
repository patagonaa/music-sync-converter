using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using MusicSyncConverter.FileProviders.Abstractions;
using System;
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
    public class WpdSyncTarget : ISyncTarget, IDisposable
    {
        private readonly object _syncLock = new object();
        private readonly IPortableDevice _device;
        private readonly IPortableDeviceContent _content;
        private readonly IPortableDeviceProperties _contentProperties;
        private readonly string _basePath;

        public WpdSyncTarget(string friendlyName, string basePath)
        {
            _device = GetDevice(friendlyName);
            _content = _device.Content();
            _contentProperties = _content.Properties();
            _basePath = basePath;
        }

        private IPortableDevice GetDevice(string toFind)
        {
            var deviceMan = new IPortableDeviceManager();

            foreach (var deviceId in deviceMan.GetDevices())
            {
                string friendlyName = deviceMan.GetDeviceFriendlyName(deviceId);
                if (friendlyName == toFind)
                {
                    var device = new IPortableDevice();
                    device.Open(deviceId, GetClientInfo());
                    return device;
                }
            }
            throw new InvalidOperationException($"Device {toFind} not found!");
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

        public Task WriteFile(string subPath, Stream content, DateTimeOffset modified, CancellationToken cancellationToken)
        {
            lock (_syncLock)
            {
                var sw = Stopwatch.StartNew();
                var fullPath = Path.Combine(_basePath, subPath);
                var directoryObjectId = CreateDirectoryStructure(Path.GetDirectoryName(fullPath));
                var fileName = Path.GetFileName(fullPath);
                Debug.WriteLine("CreateDir " + sw.ElapsedMilliseconds);
                sw.Restart();

                string? existingFileObjId = null;
                foreach (var objId in _content.EnumObjects(0, directoryObjectId).Enumerate())
                {
                    var propsToRead = new IPortableDeviceKeyCollection();
                    propsToRead.Add(WPD_OBJECT_NAME);

                    var existingFileProps = _contentProperties.GetValues(objId, propsToRead);
                    var existingFileName = existingFileProps.GetStringValue(WPD_OBJECT_NAME);
                    if (existingFileName == fileName)
                    {
                        existingFileObjId = objId;
                        break;
                    }
                }

                if (existingFileObjId != null)
                {
                    var objsToDelete = new IPortableDevicePropVariantCollection();
                    objsToDelete.Add(new Ole32.PROPVARIANT(existingFileObjId));
                    _content.Delete(DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_NO_RECURSION, objsToDelete);
                }

                Debug.WriteLine("DeleteExisting " + sw.ElapsedMilliseconds);
                sw.Restart();

                var properties = new IPortableDeviceValues();
                properties.SetStringValue(WPD_OBJECT_PARENT_ID, directoryObjectId);
                properties.SetUnsignedLargeIntegerValue(WPD_OBJECT_SIZE, checked((ulong)content.Length));
                properties.SetGuidValue(WPD_OBJECT_CONTENT_TYPE, WPD_CONTENT_TYPE_GENERIC_FILE);
                properties.SetStringValue(WPD_OBJECT_ORIGINAL_FILE_NAME, fileName);
                properties.SetStringValue(WPD_OBJECT_NAME, fileName);
                properties.SetValue(WPD_OBJECT_DATE_MODIFIED, new PROPVARIANT(modified.LocalDateTime));

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
                Debug.WriteLine("Write " + sw.ElapsedMilliseconds + "(" + (content.Length / sw.Elapsed.TotalSeconds / 1024 / 1024).ToString("F2") + "MiB/s)");
            }
            return Task.CompletedTask;
        }

        private string CreateDirectoryStructure(string fullPath)
        {
            var pathParts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
                    if (itemProperties?.Enumerate().FirstOrDefault(x => x.Item1 == WPD_OBJECT_NAME).Item2?.bstrVal == pathPart)
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

        public IFileInfo GetFileInfo(string subPath)
        {
            lock (_syncLock)
            {
                var content = _device.Content();
                var properties = content.Properties();
                var obj = GetObjectId(subPath);
            }
            return null;
        }

        public IDirectoryContents GetDirectoryContents(string subPath)
        {
            string? obj;
            lock (_syncLock)
            {
                obj = GetObjectId(subPath);
            }
            if (obj == null)
                return NotFoundDirectoryContents.Singleton;
            return new WpdDirectoryContents(obj, _syncLock, _content, _contentProperties);
        }

        private string? GetObjectId(string subPath)
        {
            var pathParts = Path.Combine(_basePath, subPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
                    if (itemProperties?.Enumerate().FirstOrDefault(x => x.Item1 == WPD_OBJECT_NAME).Item2?.bstrVal == pathPart)
                    {
                        foundChild = true;
                        currentObject = item;
                        break;
                    }
                }
                if (!foundChild)
                {
                    return null;
                }
            }
            return currentObject;
        }

        public IChangeToken Watch(string filter)
        {
            throw new NotImplementedException();
        }

        public void Delete(IFileInfo file)
        {
            if (!(file is WpdFileInfo wpdFileInfo))
            {
                throw new ArgumentException("file must be WpdFileInfo", nameof(file));
            }

            var objsToDelete = new IPortableDevicePropVariantCollection();
            objsToDelete.Add(new PROPVARIANT(wpdFileInfo.ObjectId));
            _content.Delete(DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_NO_RECURSION, objsToDelete);
        }

        private void EnumerateRecursive(string objId, IPortableDeviceContent content, IPortableDeviceProperties properties)
        {
            var keys = new IPortableDeviceKeyCollection();
            keys.Add(WPD_OBJECT_NAME);
            keys.Add(WPD_OBJECT_DATE_MODIFIED);

            var values = properties.GetValues(objId, keys);
            foreach (var (key, value) in values.Enumerate())
            {
                if (key == WPD_OBJECT_NAME && value.bstrVal == "Interner gemeinsamer Speicher")
                    return;
                Console.WriteLine($"{key}: {value.Value}");
            }

            var objectIds = content.EnumObjects(0, objId);
            foreach (var obj in objectIds.Enumerate())
            {
                EnumerateRecursive(obj, content, properties);
            }

        }

        public void Dispose()
        {
            _device.Close();
        }
    }
}
