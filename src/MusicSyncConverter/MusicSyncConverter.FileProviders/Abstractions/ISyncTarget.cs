using Microsoft.Extensions.FileProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.Abstractions
{
    public interface ISyncTarget : IFileProvider
    {
        bool IsCaseSensitive();
        Task WriteFile(string path, Stream content, DateTimeOffset modified, CancellationToken cancellationToken);
        void Delete(IFileInfo file);
        void Delete(IReadOnlyCollection<IFileInfo> files);
    }
}
