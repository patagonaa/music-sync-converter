using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter.AdbAbstraction
{
    public class AdbClient
    {
        public static readonly Encoding Encoding = Encoding.UTF8;
        private static readonly Regex _deviceRegex = new Regex(@"^(?<serial>\w+?)\t(?<state>[\w\s]+?)$", RegexOptions.Multiline);

        public AdbClient()
        {
        }

        public async Task<int> GetHostVersion()
        {
            using var client = await GetConnectedClient();
            await ExecuteAdbCommand(client, "host:version");
            return int.Parse(await ReadStringResult(client), NumberStyles.HexNumber);
        }

        public async Task<IList<(string Serial, string State)>> GetDevices()
        {
            using var client = await GetConnectedClient();
            await ExecuteAdbCommand(client, "host:devices");
            var result = await ReadStringResult(client);
            return _deviceRegex.Matches(result).Select(x => (x.Groups["serial"].Value, x.Groups["state"].Value)).ToList();
        }

        public async Task<AdbSyncClient> GetSyncClient(string serial)
        {
            var client = await GetConnectedClient();
            await ExecuteAdbCommand(client, $"host:transport:{serial}");
            await ExecuteAdbCommand(client, $"sync:");
            return new AdbSyncClient(client);
        }

        public async Task<int> Execute(string serial, string command, IEnumerable<string> parms, Stream? stdin, Stream? stdout, Stream? stderr, CancellationToken cancellationToken = default)
        {
            using var client = await GetConnectedClient();
            await ExecuteAdbCommand(client, $"host:transport:{serial}");
            await ExecuteAdbCommand(client, $"shell,v2,raw:{GetShellCommand(command, parms)}");
            if (stdin != null)
                throw new NotImplementedException();
            var stream = client.GetStream();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (type, message) = await GetMessage(stream);

                switch (type)
                {
                    case 1:
                        if (stdout != null)
                            await stdout.WriteAsync(message);
                        break;
                    case 2:
                        if (stderr != null)
                            await stderr.WriteAsync(message);
                        break;
                    case 3:
                        return message[0];
                    default:
                        throw new Exception($"Invalid Shell Command {type}");
                }
            }

            static async Task<(byte Type, byte[] Content)> GetMessage(Stream stream)
            {
                var header = new byte[5];
                await stream.ReadExact(header.AsMemory());
                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(header, 1, 4);
                }
                var bodyLength = BitConverter.ToUInt32(header, 1);
                var body = new byte[bodyLength];
                await stream.ReadExact(body.AsMemory());
                return (header[0], body);
            }
        }

        private string GetShellCommand(string command, IEnumerable<string> parms)
        {
            var sb = new StringBuilder(100);
            sb.Append(command);
            foreach (var parm in parms)
            {
                sb.Append(" '");
                sb.Append(parm.Replace("'", "'\\''"));
                sb.Append("'");
            }
            return sb.ToString();
        }

        private async Task ExecuteAdbCommand(TcpClient tcpClient, string command)
        {
            var stream = tcpClient.GetStream();
            var commandLength = Encoding.GetByteCount(command);
            var request = $"{commandLength:X4}{command}";

            await stream.WriteAsync(Encoding.GetBytes(request).AsMemory());

            var buffer = new byte[4];
            await stream.ReadExact(buffer.AsMemory());

            var responseType = Encoding.GetString(buffer);

            switch (responseType)
            {
                case "OKAY":
                    return;
                case "FAIL":
                    var response = await ReadStringResult(tcpClient);
                    throw new AdbException(response);
                default:
                    throw new InvalidOperationException($"Invalid response type {responseType}");
            }
        }

        private async Task<string> ReadStringResult(TcpClient tcpClient)
        {
            var stream = tcpClient.GetStream();

            var buffer = new byte[4];
            await stream.ReadExact(buffer.AsMemory());

            var responseLength = int.Parse(Encoding.GetString(buffer), NumberStyles.HexNumber);
            var responseBuffer = new byte[responseLength];
            await stream.ReadExact(responseBuffer.AsMemory());
            return Encoding.GetString(responseBuffer);
        }

        private async Task<TcpClient> GetConnectedClient()
        {
            var tcpClient = new TcpClient(AddressFamily.InterNetworkV6);
            tcpClient.Client.DualMode = true;
            await tcpClient.ConnectAsync(IPAddress.Loopback, 5037);
            return tcpClient;
        }
    }
}
