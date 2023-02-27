using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSyncConverter
{
    internal class ProcessStartHelper
    {
        public static async Task RunProcess(string command, IList<string> args, TextWriter? stdout = null, TextWriter? stderr = null, Action<Process>? cancelAction = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var process = new Process { StartInfo = startInfo };
            using var errorLog = new StringWriter();

            if (stdout != null)
                process.OutputDataReceived += (o, e) => stdout.WriteLine(e.Data);

            process.ErrorDataReceived += (o, e) => { errorLog.WriteLine(e.Data); stderr?.WriteLine(e.Data); };

            cancellationToken.ThrowIfCancellationRequested();

            using (cancellationToken.Register(() => cancelAction?.Invoke(process)))
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(CancellationToken.None);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Exit Code {process.ExitCode} while running {command} \"{string.Join("\" \"", args)}\"." + Environment.NewLine + Environment.NewLine + errorLog.ToString());
            }
        }
    }
}
