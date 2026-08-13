using System.Diagnostics;
using System.Text;

namespace Talah.Harness.Adapters.Codex;

internal sealed class CodexProcessTransport : ICodexTransport
{
    private readonly Process _process;

    private CodexProcessTransport(Process process)
    {
        _process = process;
    }

    public TextWriter Input => _process.StandardInput;

    public TextReader Output => _process.StandardOutput;

    public TextReader Error => _process.StandardError;

    public Task Completion => _process.WaitForExitAsync();

    public static CodexProcessTransport Start(
        string executable,
        IReadOnlyDictionary<string, string> environment)
    {
        executable = ResolveExecutable(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start Codex App Server.");
        }

        process.StandardInput.AutoFlush = true;
        return new CodexProcessTransport(process);
    }

    internal static string ResolveExecutable(string executable)
    {
        if (!OperatingSystem.IsWindows() || executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        var explicitDirectory = Path.GetDirectoryName(executable);
        var fileName = Path.GetFileNameWithoutExtension(executable);
        var searchDirectories = !string.IsNullOrEmpty(explicitDirectory)
            ? new[] { Path.GetFullPath(explicitDirectory) }
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in searchDirectories)
        {
            var directExe = Path.Combine(directory, fileName + ".exe");
            if (File.Exists(directExe))
            {
                return directExe;
            }

            var nativePackageRoot = Path.Combine(
                directory,
                "node_modules",
                "@openai",
                "codex",
                "node_modules");
            if (!Directory.Exists(nativePackageRoot))
            {
                continue;
            }

            try
            {
                var nativeExe = Directory.EnumerateFiles(nativePackageRoot, "codex.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Contains(
                        $"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase));
                if (nativeExe is not null)
                {
                    return nativeExe;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Continue to other PATH entries. Diagnostics will come from Process.Start if none work.
            }
        }

        return executable;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
