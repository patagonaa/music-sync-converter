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
        Task WriteFile(string path, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default);
        Task Delete(IFileInfo file, CancellationToken cancellationToken);
        Task Delete(IReadOnlyCollection<IFileInfo> files, CancellationToken cancellationToken);
        bool IsHidden(string path, bool recurse);
        Task Complete(CancellationToken cancellationToken);
    }
}
