using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MusicSyncConverter.AdbAbstraction
{
    public class AdbClient
    {
        public static readonly Encoding CommandEncoding = Encoding.ASCII;
        private static readonly Regex _deviceRegex = new Regex(@"^(?<serial>\w+?)\t(?<state>[\w\s]+?)$", RegexOptions.Multiline);

        public AdbClient()
        {
        }

        public async Task<int> GetHostVersion()
        {
            using var client = await GetConnectedClient();
            await ExecuteCommand(client, "host:version");
            return int.Parse(await ReadStringResult(client), NumberStyles.HexNumber);
        }

        public async Task<IList<(string Serial, string State)>> GetDevices()
        {
            using var client = await GetConnectedClient();
            await ExecuteCommand(client, "host:devices");
            var result = await ReadStringResult(client);
            return _deviceRegex.Matches(result).Select(x => (x.Groups["serial"].Value, x.Groups["state"].Value)).ToList();
        }

        public async Task<AdbSyncClient> GetSyncClient(string serial)
        {
            var client = await GetConnectedClient();
            await ExecuteCommand(client, $"host:transport:{serial}");
            await ExecuteCommand(client, $"sync:");
            return new AdbSyncClient(client);
        }

        private async Task ExecuteCommand(TcpClient tcpClient, string command)
        {
            var stream = tcpClient.GetStream();
            var commandLength = CommandEncoding.GetByteCount(command);
            var request = $"{commandLength:X4}{command}";

            await stream.WriteAsync(CommandEncoding.GetBytes(request).AsMemory());

            var buffer = new byte[4];
            await stream.ReadExact(buffer.AsMemory());

            var responseType = CommandEncoding.GetString(buffer);

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

            var responseLength = int.Parse(CommandEncoding.GetString(buffer), NumberStyles.HexNumber);
            var responseBuffer = new byte[responseLength];
            await stream.ReadExact(responseBuffer.AsMemory());
            return CommandEncoding.GetString(responseBuffer);
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
