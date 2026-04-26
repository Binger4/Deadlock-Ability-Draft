using Spectre.Console;
using System.Diagnostics;

namespace DeadPacker
{
    internal class ResourceCompiler
    {
        private readonly CompileConfig config;

        public ResourceCompiler(CompileConfig config)
        {
            if (config.CompilerPath == null)
            {
                throw new ArgumentException("resource_compiler_path is not specified");
            }
            if (config.CompilerPath == null)
            {
                throw new ArgumentException("addon_content_directory is not specified");
            }
            this.config = config;
        }

        private Process GetProcess()
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = config.CompilerPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            return process;
        }

        public async Task CompileContentDirectory()
        {
            await Task.Delay(100); // Slight delay to give some time for file locks to be released
            Log.Info($"Compiling directory {Log.FormatPath(config.ContentDirectory!)}");

            using var process = GetProcess();
            process.StartInfo.Arguments = $"-r -i \"{config.ContentDirectory!.TrimEnd('/', '\\')}\\**\" -danger_mode_ignore_schema_mismatches";

            AnsiConsole.WriteLine();

            process.Start();
            process.BeginOutputReadLine();
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    AnsiConsole.WriteLine(e.Data);
                }
            };
            await process.WaitForExitAsync();

            AnsiConsole.WriteLine();

            if (process.ExitCode == 0)
            {
                Log.Info($"Compiled {Log.FormatPath(config.ContentDirectory!)}");
            }
            else
            {
                Log.Error($"Failed to compile {Log.FormatPath(config.ContentDirectory!)}. Exit code: {process.ExitCode}");
            }
        }
    }
}
