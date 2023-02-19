using Microsoft.Extensions.Configuration;
using MusicSyncConverter.Config;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // this is required so Unicode chars work on (Windows) machines where the system locale uses a non-Unicode character set (which is usually the case).
            // there is a system-wide "Beta: Use Unicode UTF-8 for worldwide language support" flag, but it is disabled by default because it still breaks a bunch of stuff.
            Console.OutputEncoding = Encoding.UTF8;

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

            var cts = new CancellationTokenSource();

            try
            {
                // only available on Windows
                _ = Console.KeyAvailable;

                // we can't use Console.CancelKeyPress as without Console.TreatControlCAsInput all child processes get the Ctrl+C signal as well, which we don't want.
                var cancelThread = new Thread(() => CancelThread(cts));
                cancelThread.Start();
            }
            catch (InvalidOperationException)
            {
                Console.CancelKeyPress += (o, args) => { cts.Cancel(); args.Cancel = true; };
            }

            var logger = new MemoryLogger();
            try
            {
                var tempFileService = new TempFileService();
                tempFileService.CleanupTempDir(cts.Token);
                using (var tempFileSession = tempFileService.GetNewSession())
                {
                    var service = new SyncService(tempFileSession, logger);
                    await service.Run(config, cts.Token);
                }
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
            finally
            {
                foreach (var fileGroup in logger.Messages.Distinct().GroupBy(x => x.Filename).OrderBy(x => x.Key))
                {
                    Console.WriteLine($"[{fileGroup.Key}]");
                    foreach (var item in fileGroup.OrderByDescending(x => x.LogLevel).ThenBy(x => x.Message))
                    {
                        Console.WriteLine($"\t{item.LogLevel}: {item.Message.ReplaceLineEndings($"{Environment.NewLine}\t")}");
                    }
                    Console.WriteLine();
                }
            }
            cts.Cancel();
        }

        private static void CancelThread(CancellationTokenSource cts)
        {
            Console.TreatControlCAsInput = true;
            while (!cts.IsCancellationRequested)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(100);
                    continue;
                }
                var key = Console.ReadKey(true);
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
                {
                    Console.WriteLine("Cancelling...");
                    cts.Cancel();
                }
            }
            Console.TreatControlCAsInput = false;
        }
    }
}
