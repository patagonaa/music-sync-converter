using Microsoft.Extensions.Configuration;
using MusicSyncConverter.Config;
using System;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if(args.Length != 1)
            {
                Console.WriteLine("no config file supplied!");
                return;
            }

            var configRoot = new ConfigurationBuilder()
                .AddJsonFile(args[0])
                .Build();

            var config = configRoot.Get<SyncConfig>();

            var service = new SyncService();
            await service.Run(config);
        }
    }
}
