using Microsoft.Extensions.FileProviders;
using System.Collections;
using System.Collections.Generic;
using Vanara.PInvoke;

namespace MusicSyncConverter.FileProviders.Wpd
{
    internal class WpdDirectoryContents : IDirectoryContents
    {
        private readonly string _objectId;
        private readonly object _synclock;
        private readonly PortableDeviceApi.IPortableDeviceContent _content;
        private readonly PortableDeviceApi.IPortableDeviceProperties _contentProperties;

        public WpdDirectoryContents(string objectId, object synclock, PortableDeviceApi.IPortableDeviceContent content, PortableDeviceApi.IPortableDeviceProperties contentProperties)
        {
            _objectId = objectId;
            _synclock = synclock;
            _content = content;
            _contentProperties = contentProperties;
        }

        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator()
        {
            // we _could_ yield return here instead, but then we would hold the lock as long as we're enumerating, which would suck.
            // for example, you wouldn't be able to do file operations in a foreach loop over the directory.
            var fileInfos = new List<IFileInfo>(); 
            lock (_synclock)
            {
                var children = _content.EnumObjects(0, _objectId);
                foreach (var childId in children.Enumerate())
                {
                    fileInfos.Add(new WpdFileInfo(childId, _content, _contentProperties));
                }
            }
            return fileInfos.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
