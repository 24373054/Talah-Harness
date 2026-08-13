using System.Diagnostics;
using System.Text;
using Talah.Harness.Runtime;

namespace Talah.Harness.Adapters.Codex;

internal sealed class CodexProcessTransport : ICodexTransport
{
    private readonly Process _process;
    private readonly WindowsJobProcess _jobProcess;
    private readonly WindowsJobObject _job;

    private CodexProcessTransport(WindowsJobProcess jobProcess, WindowsJobObject job)
    {
        _jobProcess = jobProcess;
        _process = jobProcess.Process;
        _job = job;
    }

    public TextWriter Input => _jobProcess.StandardInput;

    public TextReader Output => _jobProcess.StandardOutput;

    public TextReader Error => _jobProcess.StandardError;

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

        startInfo.Environment.Clear();
        foreach (string name in EnvironmentAllowList) CopyEnvironmentIfPresent(startInfo, name);
        foreach (KeyValuePair<string, string> pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var job = new WindowsJobObject();
        WindowsJobProcess? jobProcess = null;
        try
        {
            jobProcess = WindowsJobProcess.Start(startInfo, job);
            jobProcess.StandardInput.AutoFlush = true;
            return new CodexProcessTransport(jobProcess, job);
        }
        catch
        {
            jobProcess?.Dispose();
            job.Dispose();
            throw;
        }
    }

    internal static string ResolveExecutable(string executable)
    {
        if (!OperatingSystem.IsWindows() || executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        string? explicitDirectory = Path.GetDirectoryName(executable);
        string fileName = Path.GetFileNameWithoutExtension(executable);
        string[] searchDirectories = !string.IsNullOrEmpty(explicitDirectory)
            ? [Path.GetFullPath(explicitDirectory)]
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string directory in searchDirectories)
        {
            string directExe = Path.Combine(directory, fileName + ".exe");
            if (File.Exists(directExe))
            {
                return directExe;
            }

            string nativePackageRoot = Path.Combine(
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
                string? nativeExe = Directory.EnumerateFiles(nativePackageRoot, "codex.exe", SearchOption.AllDirectories)
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
            _jobProcess.StandardInput.Close();
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _job.Terminate();
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _job.Dispose();
            _jobProcess.Dispose();
        }
    }

    private static void CopyEnvironmentIfPresent(ProcessStartInfo startInfo, string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value)) startInfo.Environment[name] = value;
    }

    private static string[] EnvironmentAllowList { get; } =
    [
        "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "PATH", "PATHEXT",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"
    ];
}
