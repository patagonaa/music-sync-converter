using Microsoft.Extensions.Configuration;
using MusicSyncConverter.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("no config file(s) supplied!");
                return;
            }

            var configBuilder = new ConfigurationBuilder();
            foreach (var configPath in args)
            {
                configBuilder.AddJsonFile(configPath);
            }

            var configRoot = configBuilder.Build();

            var config = configRoot.Get<SyncConfig>();

            if (config.SourceExtensions == null || config.SourceExtensions.Count == 0)
            {
                config.SourceExtensions = new List<string>
                    {
                        ".mp3",
                        ".ogg",
                        ".m4a",
                        ".flac",
                        ".opus",
                        ".wma",
                        ".wav"
                    };
            }

            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (o, e) => { cts.Cancel(); e.Cancel = true; };

            var service = new SyncService();
            try
            {
                await service.Run(config, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException ex)
            {
                if (ex.Flatten().InnerExceptions.Any(x => x is not OperationCanceledException))
                {
                    throw;
                }
            }
        }
    }
}
