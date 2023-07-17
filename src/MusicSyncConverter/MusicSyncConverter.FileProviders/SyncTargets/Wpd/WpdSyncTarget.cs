using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Vanara.Extensions;
using Vanara.PInvoke;
using static Vanara.PInvoke.Ole32;
using static Vanara.PInvoke.PortableDeviceApi;

namespace MusicSyncConverter.FileProviders.SyncTargets.Wpd
{
    // https://docs.microsoft.com/en-us/windows/win32/windows-portable-devices
    // https://github.com/teapottiger/WPDApi/blob/62be7c4acc6104aef108769b4496b35b0a7fbd53/PortableDevices/PortableDevice.cs
    // https://github.com/dahall/Vanara/blob/76722fbcf5c1f90dccee9751dc0367641e37b4f9/UnitTests/PInvoke/PortableDeviceApi/PortableDeviceApiTests.cs
    [SupportedOSPlatform("windows")]
    public class WpdSyncTarget : ISyncTarget, IDisposable
    {
        private readonly object _syncLock = new object();
        private readonly IPortableDevice _device;
        private readonly IPortableDeviceContent _content;
        private readonly IPortableDeviceProperties _contentProperties;
        private readonly string _basePath;
        private readonly PathComparer _pathComparer;
        private readonly Dictionary<NormalizedPath, IList<SyncTargetFileInfo>?> _directoryContentsCache;
        private readonly Dictionary<NormalizedPath, string> _objectIdCache;

        public WpdSyncTarget(string friendlyName, string basePath)
        {
            _device = GetDevice(friendlyName);
            _content = _device.Content();
            _contentProperties = _content.Properties();
            _basePath = basePath;
            _pathComparer = new PathComparer(IsCaseSensitive().Result);
            _directoryContentsCache = new Dictionary<NormalizedPath, IList<SyncTargetFileInfo>?>(_pathComparer);
            _objectIdCache = new Dictionary<NormalizedPath, string>(_pathComparer);
        }

        private void GetFormats()
        {
            var allFormats = typeof(PortableDeviceApi)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.Name.StartsWith("WPD_OBJECT_FORMAT_"))
                .ToDictionary(x => (Guid)x.GetValue(null)!, x => x);

            var allFormatProps = typeof(PortableDeviceApi)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.PropertyType == typeof(PROPERTYKEY))
                .ToDictionary(x => (PROPERTYKEY)x.GetValue(null)!, x => x);

            var allFormatPropAttrs = typeof(PortableDeviceApi)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.Name.StartsWith("WPD_PROPERTY_ATTRIBUTE_"))
                .ToDictionary(x => (PROPERTYKEY)x.GetValue(null)!, x => x);

            var caps = _device.Capabilities();

            var all = new Dictionary<Guid, Dictionary<PROPERTYKEY, Dictionary<PROPERTYKEY, object>>>();
            foreach (var format in caps.GetSupportedFormats(WPD_CONTENT_TYPE_AUDIO).Enumerate().Select(x => x.puuid ?? throw new ArgumentNullException()))
            {
                if (!allFormats.TryGetValue(format, out var formatName))
                    continue;
                var formatProps = new Dictionary<PROPERTYKEY, Dictionary<PROPERTYKEY, object>>();

                foreach (var formatProp in caps.GetSupportedFormatProperties(format).Enumerate())
                {
                    var propAttrs = new Dictionary<PROPERTYKEY, object>();
                    foreach (var attrs in caps.GetFixedPropertyAttributes(format, formatProp).Enumerate())
                    {
                        if (attrs.Item1 == WPD_PROPERTY_ATTRIBUTE_ENUMERATION_ELEMENTS)
                        {
                            var attrEnumElements = ((IPortableDevicePropVariantCollection)attrs.Item2.punkVal).Enumerate();
                            if (formatProp == WPD_MEDIA_BITRATE_TYPE)
                            {
                                propAttrs.Add(attrs.Item1, attrEnumElements.Select(x => (WPD_BITRATE_TYPES)x.uintVal).ToArray());
                            }
                            else
                            {
                                propAttrs.Add(attrs.Item1, attrEnumElements.Select(x => x.Value).ToArray());
                            }
                        }
                        else
                        {
                            propAttrs.Add(attrs.Item1, attrs.Item2.Value);
                        }

                    }
                    formatProps.Add(formatProp, propAttrs);
                }

                all.Add(format, formatProps);
            }
            var clearText = all
                .ToDictionary(
                fmt => allFormats[fmt.Key].Name,
                fmt => fmt.Value
                    .ToDictionary(
                        prop => allFormatProps.GetValueOrDefault(prop.Key)?.Name ?? prop.Key.ToString(),
                        prop => prop.Value
                            .ToDictionary(
                                attr => allFormatPropAttrs[attr.Key].Name,
                                attr => attr.Value
                            )
                        )
                    );
        }

        private static IPortableDevice GetDevice(string toFind)
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

        private static IPortableDeviceValues GetClientInfo()
        {
            var clientInfo = new IPortableDeviceValues();
            clientInfo.SetStringValue(WPD_CLIENT_NAME, "MusicSyncConverter");
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_MAJOR_VERSION, 1);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_MINOR_VERSION, 0);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_REVISION, 0);
            clientInfo.SetUnsignedIntegerValue(WPD_CLIENT_DESIRED_ACCESS, ACCESS_MASK.GENERIC_READ | ACCESS_MASK.GENERIC_WRITE);
            return clientInfo;
        }

        public Task<bool> IsCaseSensitive()
        {
            return Task.FromResult(false); // TODO do we even care?
        }

        public Task WriteFile(string path, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default)
        {
            lock (_syncLock)
            {
                var sw = Stopwatch.StartNew();
                var directoryObjectId = CreateDirectoryStructure(Path.GetDirectoryName(path) ?? throw new ArgumentNullException(nameof(path)));
                var fileName = Path.GetFileName(path);
                Debug.WriteLine("CreateDir " + sw.ElapsedMilliseconds + "ms");
                sw.Restart();

                var existingFileObjId = GetObjectId(fileName, directoryObjectId);
                if (existingFileObjId != null)
                {
                    var objsToDelete = new IPortableDevicePropVariantCollection();
                    objsToDelete.Add(new PROPVARIANT(existingFileObjId));
                    _content.Delete(DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_NO_RECURSION, objsToDelete);
                }

                Debug.WriteLine("DeleteExisting " + sw.ElapsedMilliseconds + "ms");
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

        public Task<SyncTargetFileInfo?> GetFileInfo(string subPath, CancellationToken cancellationToken)
        {
            lock (_syncLock)
            {
                var obj = GetObjectId(subPath);
                if (obj == null)
                    return Task.FromResult<SyncTargetFileInfo?>(null);

                return Task.FromResult<SyncTargetFileInfo?>(GetFileInfoInternal(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(subPath)) ?? throw new ArgumentException("Invalid Path"), obj));
            }
        }

        public Task<IList<SyncTargetFileInfo>?> GetDirectoryContents(string subPath, CancellationToken cancellationToken)
        {
            var normalizedSubPath = new NormalizedPath(subPath);
            lock (_syncLock)
            {
                var sw = Stopwatch.StartNew();
                if (!_directoryContentsCache.TryGetValue(normalizedSubPath, out IList<SyncTargetFileInfo>? directoryContents))
                {
                    var obj = GetObjectId(subPath);
                    if (obj == null)
                    {
                        directoryContents = null;
                    }
                    else
                    {
                        directoryContents = new List<SyncTargetFileInfo>();
                        var children = _content.EnumObjects(0, obj);
                        foreach (var childId in children.Enumerate())
                        {
                            directoryContents.Add(GetFileInfoInternal(subPath, childId));
                        }
                    }
                    _directoryContentsCache.TryAdd(normalizedSubPath, directoryContents);
                }
                sw.Stop();
                Debug.WriteLine("GetDirectoryContents " + sw.ElapsedMilliseconds + "ms");
                return Task.FromResult(directoryContents);
            }
        }

        private SyncTargetFileInfo GetFileInfoInternal(string directoryPath, string obj)
        {
            var propertiesToFetch = new IPortableDeviceKeyCollection();
            propertiesToFetch.Add(WPD_OBJECT_NAME);
            propertiesToFetch.Add(WPD_OBJECT_DATE_MODIFIED);
            propertiesToFetch.Add(WPD_OBJECT_CONTENT_TYPE);

            var values = _contentProperties.GetValues(obj, propertiesToFetch);

            string? name = null;
            var lastModified = DateTimeOffset.MinValue;
            var isDirectory = false;
            foreach (var (key, value) in values.Enumerate())
            {
                if (key == WPD_OBJECT_NAME)
                {
                    name = value.bstrVal;
                }

                if (key == WPD_OBJECT_DATE_MODIFIED)
                {
                    lastModified = value.date;
                }

                if (key == WPD_OBJECT_CONTENT_TYPE)
                {
                    if (value.puuid == WPD_CONTENT_TYPE_FOLDER)
                    {
                        isDirectory = true;
                    }
                }
            }
            if (name == null)
            {
                throw new InvalidOperationException($"object with ID {obj} has no name");
            }

            return new SyncTargetFileInfo(Path.Join(directoryPath, name), isDirectory, lastModified);
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
                    string searchPath = Path.Join(currentPath, pathPart);
                    NormalizedPath normalizedSearchPath = new NormalizedPath(searchPath);
                    if (_objectIdCache.TryGetValue(normalizedSearchPath, out var foundObjId))
                    {
                        currentObject = foundObjId;
                        currentPath = searchPath;
                    }
                    else
                    {
                        var items = _content.EnumObjects(0, currentObject);
                        string? foundChild = null;
                        foreach (var item in items.Enumerate())
                        {
                            var itemProperties = _contentProperties.GetValues(item, keys);
                            var name = itemProperties?.GetStringValue(WPD_OBJECT_NAME);

                            if (_pathComparer.FileNameEquals(name, pathPart))
                            {
                                _objectIdCache[normalizedSearchPath] = item;
                                foundChild = item;
                            }
                            else if (name != null)
                            {
                                _objectIdCache[new NormalizedPath(Path.Join(currentPath, name))] = item;
                            }
                        }
                        if (foundChild != null)
                        {
                            currentObject = foundChild;
                            currentPath = searchPath;
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

        public Task<bool> IsHidden(string path, bool recurse)
        {
            var pathParts = PathUtils.GetPathStack(path);
            return Task.FromResult(recurse ? pathParts.Any(x => x.StartsWith('.')) : pathParts.First().StartsWith('.'));
        }

        public Task Delete(IReadOnlyCollection<SyncTargetFileInfo> files, CancellationToken cancellationToken)
        {
            if (files.Count == 0)
                return Task.CompletedTask;
            lock (_syncLock)
            {
                var sw = Stopwatch.StartNew();
                var objsToDelete = new IPortableDevicePropVariantCollection();
                foreach (var wpdFile in files)
                {
                    objsToDelete.Add(new PROPVARIANT(GetObjectId(wpdFile.Path)));
                }
                _content.Delete(DELETE_OBJECT_OPTIONS.PORTABLE_DEVICE_DELETE_NO_RECURSION, objsToDelete);
                ClearCaches();
                sw.Stop();
                Debug.WriteLine("Delete {0} in {1}ms", files.Count, sw.ElapsedMilliseconds);
            }
            return Task.CompletedTask;
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
