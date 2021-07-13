﻿using Microsoft.Extensions.Configuration;
using MusicSyncConverter.Config;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("no config file supplied!");
                return;
            }

            var configRoot = new ConfigurationBuilder()
                .AddJsonFile(args[0])
                .Build();

            var config = configRoot.Get<SyncConfig>();

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
