using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.AdbAbstraction
{
    public class AdbSyncClient : IDisposable
    {
        public static readonly Encoding PathEncoding = Encoding.UTF8;
        private readonly TcpClient _tcpClient;

        internal AdbSyncClient(TcpClient tcpClient)
        {
            _tcpClient = tcpClient;
        }

        public async Task Push(string path, UnixFileMode permissions, DateTimeOffset modifiedDate, Stream inStream, CancellationToken cancellationToken = default)
        {
            var adbStream = _tcpClient.GetStream();

            var permissionsMask = 0x01FF; // 0777;
            var pathWithPermissions = $"{path},0{Convert.ToString((int)permissions & permissionsMask, 8)}";
            await SendRequestWithPath(adbStream, "SEND", pathWithPermissions);

            const int maxChunkSize = 64 * 1024; // SYNC_DATA_MAX
            var buffer = new byte[maxChunkSize].AsMemory();
            int readBytes;
            while ((readBytes = await inStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await SendRequestWithLength(adbStream, "DATA", readBytes);
                await adbStream.WriteAsync(buffer.Slice(0, readBytes));
            }
            await SendRequestWithLength(adbStream, "DONE", (int)modifiedDate.ToUnixTimeSeconds());
            await GetResponse(adbStream);
            await ReadInt32(adbStream);
        }

        public async Task<IList<StatEntry>> List(string path)
        {
            var stream = _tcpClient.GetStream();
            await SendRequestWithPath(stream, "LIST", path);

            var toReturn = new List<StatEntry>();
            while (true)
            {
                var response = await GetResponse(stream);
                if (response == "DONE")
                {
                    // ADB sends an entire (empty) stat entry when done, so we have to skip it
                    var ignoreBuf = new byte[16];
                    await stream.ReadExact(ignoreBuf);
                    break;
                }
                else if (response == "DENT")
                {
                    var statEntry = await ReadStatEntry(stream);
                    statEntry.Path = await ReadString(stream, PathEncoding);
                    toReturn.Add(statEntry);
                }
                else if (response != "STAT")
                {
                    throw new InvalidOperationException($"Invalid Response Type {response}");
                }
            }

            return toReturn;
        }

        public async Task<StatEntry> Stat(string path)
        {
            var stream = _tcpClient.GetStream();
            await SendRequestWithPath(stream, "STAT", path);
            var response = await GetResponse(stream);
            if (response != "STAT")
                throw new InvalidOperationException($"Invalid Response Type {response}");
            var statEntry = await ReadStatEntry(stream);
            statEntry.Path = path;
            return statEntry;
        }

        private async Task<StatEntry> ReadStatEntry(Stream stream)
        {
            var fileMode = await ReadInt32(stream);
            var fileSize = await ReadInt32(stream);
            var fileModifiedTime = await ReadInt32(stream);
            return new StatEntry
            {
                Mode = (UnixFileMode)fileMode,
                Size = fileSize,
                ModifiedTime = DateTime.UnixEpoch.AddSeconds(fileModifiedTime)
            };
        }

        private async Task SendRequestWithPath(Stream stream, string requestType, string path)
        {
            int pathLengthBytes = PathEncoding.GetByteCount(path);
            await SendRequestWithLength(stream, requestType, pathLengthBytes);

            var pathBytes = PathEncoding.GetBytes(path);
            await stream.WriteAsync(pathBytes.AsMemory());
        }

        private async Task SendRequestWithLength(Stream stream, string requestType, int length)
        {
            var requestBytes = new byte[8];
            AdbClient.CommandEncoding.GetBytes(requestType.AsSpan(), requestBytes.AsSpan(0, 4));

            BitConverter.GetBytes(length).CopyTo(requestBytes, 4);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(requestBytes, 4, 4);
            }

            await stream.WriteAsync(requestBytes.AsMemory());
        }

        private async Task<string> GetResponse(Stream stream)
        {
            var responseTypeBuffer = new byte[4];
            await stream.ReadExact(responseTypeBuffer.AsMemory());
            var responseType = AdbClient.CommandEncoding.GetString(responseTypeBuffer);
            if (responseType == "FAIL")
            {
                throw new AdbException(await ReadString(stream, AdbClient.CommandEncoding));
            }
            return responseType;
        }

        private async Task<string> ReadString(Stream stream, Encoding encoding)
        {
            var responseLength = await ReadInt32(stream);
            var responseBuffer = new byte[responseLength];
            await stream.ReadExact(responseBuffer.AsMemory());
            return encoding.GetString(responseBuffer);
        }

        private async Task<int> ReadInt32(Stream stream)
        {
            var buffer = new byte[4];
            await stream.ReadExact(buffer.AsMemory());
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(buffer);
            }
            return BitConverter.ToInt32(buffer);
        }

        public void Dispose()
        {
            _tcpClient.Dispose();
        }
    }
}
