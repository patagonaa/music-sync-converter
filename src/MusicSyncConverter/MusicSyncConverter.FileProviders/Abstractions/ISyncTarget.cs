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
        Task Delete(IFileInfo file, CancellationToken cancellationToken);
        Task Delete(IReadOnlyCollection<IFileInfo> files, CancellationToken cancellationToken);
        Task Complete(CancellationToken cancellationToken);
    }
}
