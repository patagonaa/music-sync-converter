using Microsoft.Extensions.FileProviders;
using System;
using System.IO;
using Vanara.PInvoke;

namespace MusicSyncConverter.FileProviders.Wpd
{
    internal class WpdFileInfo : IFileInfo
    {
        internal string ObjectId { get; }

        public WpdFileInfo(string objectId, object synclock, PortableDeviceApi.IPortableDeviceContent content, PortableDeviceApi.IPortableDeviceProperties contentProperties)
        {
            ObjectId = objectId;

            var propertiesToFetch = new PortableDeviceApi.IPortableDeviceKeyCollection();
            propertiesToFetch.Add(PortableDeviceApi.WPD_OBJECT_NAME);
            propertiesToFetch.Add(PortableDeviceApi.WPD_OBJECT_SIZE);
            propertiesToFetch.Add(PortableDeviceApi.WPD_OBJECT_DATE_MODIFIED);
            propertiesToFetch.Add(PortableDeviceApi.WPD_OBJECT_CONTENT_TYPE);

            var values = contentProperties.GetValues(objectId, propertiesToFetch);

            Length = -1;
            LastModified = DateTimeOffset.MinValue;
            foreach (var (key, value) in values.Enumerate())
            {
                if (key == PortableDeviceApi.WPD_OBJECT_NAME)
                {
                    Name = value.bstrVal;
                }

                if (key == PortableDeviceApi.WPD_OBJECT_SIZE)
                {
                    Length = checked((uint)value.uhVal);
                }

                if (key == PortableDeviceApi.WPD_OBJECT_DATE_MODIFIED)
                {
                    LastModified = value.date;
                }

                if (key == PortableDeviceApi.WPD_OBJECT_CONTENT_TYPE)
                {
                    if (value.puuid == PortableDeviceApi.WPD_CONTENT_TYPE_FOLDER)
                    {
                        IsDirectory = true;
                    }
                }
            }
            if (Name == null)
            {
                throw new InvalidOperationException($"object with ID {objectId} has no name");
            }
        }

        public bool Exists => true;

        public long Length { get; }

        public string? PhysicalPath => null;

        public string Name { get; }

        public DateTimeOffset LastModified { get; }

        public bool IsDirectory { get; }

        public Stream CreateReadStream()
        {
            throw new NotImplementedException();
        }
    }
}
