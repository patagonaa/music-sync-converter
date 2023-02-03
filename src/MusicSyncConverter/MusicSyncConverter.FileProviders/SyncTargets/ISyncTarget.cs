using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.FileProviders.SyncTargets
{
    public interface ISyncTarget
    {
        Task<SyncTargetFileInfo?> GetFileInfo(string subpath, CancellationToken cancellationToken = default);
        Task<IList<SyncTargetFileInfo>?> GetDirectoryContents(string subpath, CancellationToken cancellationToken = default);
        Task<bool> IsCaseSensitive();
        Task WriteFile(string path, Stream content, DateTimeOffset? modified = null, CancellationToken cancellationToken = default);
        Task Delete(IReadOnlyCollection<SyncTargetFileInfo> files, CancellationToken cancellationToken);
        Task<bool> IsHidden(string path, bool recurse);
        Task Complete(CancellationToken cancellationToken);
    }
}
