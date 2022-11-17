using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace MusicSyncConverter.AdbAbstraction
{
    internal static class StreamExtensions
    {
        internal static async Task ReadExact(this Stream stream, Memory<byte> memory, CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < memory.Length;)
            {
                i += await stream.ReadAsync(memory.Slice(i), cancellationToken);
            }
        }
    }
}
