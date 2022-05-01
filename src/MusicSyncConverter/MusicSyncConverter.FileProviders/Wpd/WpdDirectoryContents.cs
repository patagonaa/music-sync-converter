using Microsoft.Extensions.FileProviders;
using System.Collections;
using System.Collections.Generic;
using Vanara.PInvoke;

namespace MusicSyncConverter.FileProviders.Wpd
{
    internal class WpdDirectoryContents : IDirectoryContents
    {
        private readonly string _objectId;
        private readonly PortableDeviceApi.IPortableDeviceContent _content;
        private readonly PortableDeviceApi.IPortableDeviceProperties _contentProperties;
        private readonly List<IFileInfo> _fileInfos;

        public WpdDirectoryContents(string objectId, object synclock, PortableDeviceApi.IPortableDeviceContent content, PortableDeviceApi.IPortableDeviceProperties contentProperties)
        {
            _objectId = objectId;
            _content = content;
            _contentProperties = contentProperties;
            _fileInfos = new List<IFileInfo>();
            var children = _content.EnumObjects(0, _objectId);
            foreach (var childId in children.Enumerate())
            {
                _fileInfos.Add(new WpdFileInfo(childId, synclock, _content, _contentProperties));
            }
        }

        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator()
        {
            return _fileInfos.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
