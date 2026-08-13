using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Talah.Harness.Runtime;

/// <summary>
/// Creates a redirected Windows process suspended, assigns it to a kill-on-close Job Object,
/// and only then allows its first instruction to execute.
/// </summary>
public sealed partial class WindowsJobProcess : IDisposable
{
    private readonly StreamWriter _standardInput;
    private readonly StreamReader _standardOutput;
    private readonly StreamReader _standardError;
    private bool _disposed;

    private WindowsJobProcess(
        Process process,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        Process = process;
        _standardInput = standardInput;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    public Process Process { get; }
    public StreamWriter StandardInput => _standardInput;
    public StreamReader StandardOutput => _standardOutput;
    public StreamReader StandardError => _standardError;

    public static unsafe WindowsJobProcess Start(ProcessStartInfo startInfo, WindowsJobObject job)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(job);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Suspended Job Object process creation requires Windows.");
        if (startInfo.UseShellExecute) throw new ArgumentException("Job-contained processes cannot use shell execution.", nameof(startInfo));
        if (string.IsNullOrWhiteSpace(startInfo.FileName)) throw new ArgumentException("An executable is required.", nameof(startInfo));

        nint stdinRead = 0;
        nint stdinWrite = 0;
        nint stdoutRead = 0;
        nint stdoutWrite = 0;
        nint stderrRead = 0;
        nint stderrWrite = 0;
        nint processHandle = 0;
        nint threadHandle = 0;
        Process? process = null;
        StreamWriter? input = null;
        StreamReader? output = null;
        StreamReader? error = null;
        bool processCreated = false;

        try
        {
            CreateRedirectPipe(out stdinRead, out stdinWrite, parentReads: false);
            CreateRedirectPipe(out stdoutRead, out stdoutWrite, parentReads: true);
            CreateRedirectPipe(out stderrRead, out stderrWrite, parentReads: true);

            var startup = new NativeMethods.STARTUPINFO
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                dwFlags = NativeMethods.STARTF_USESTDHANDLES,
                hStdInput = stdinRead,
                hStdOutput = stdoutWrite,
                hStdError = stderrWrite
            };
            string executable = ResolveExecutable(startInfo.FileName);
            char[] commandLine = (BuildCommandLine(executable, startInfo.ArgumentList).Append('\0').ToString()).ToCharArray();
            char[] environment = BuildEnvironmentBlock(startInfo.Environment);
            NativeMethods.PROCESS_INFORMATION processInformation;
            fixed (char* commandLinePointer = commandLine)
            fixed (char* environmentPointer = environment)
            {
                bool created = NativeMethods.CreateProcess(
                    executable,
                    commandLinePointer,
                    0,
                    0,
                    true,
                    NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_NO_WINDOW,
                    environmentPointer,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startup,
                    out processInformation);
                if (!created)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create '{Path.GetFileName(executable)}' suspended.");
            }

            processCreated = true;
            processHandle = processInformation.hProcess;
            threadHandle = processInformation.hThread;
            int processId = checked((int)processInformation.dwProcessId);
            using (var safeProcessHandle = new SafeProcessHandle(processHandle, ownsHandle: false))
                job.Assign(safeProcessHandle, processId);

            process = Process.GetProcessById(processId);
            input = new StreamWriter(
                new FileStream(new SafeFileHandle(stdinWrite, ownsHandle: true), FileAccess.Write, 4_096, isAsync: false),
                startInfo.StandardInputEncoding ?? new UTF8Encoding(false)) { AutoFlush = false };
            stdinWrite = 0;
            output = new StreamReader(
                new FileStream(new SafeFileHandle(stdoutRead, ownsHandle: true), FileAccess.Read, 4_096, isAsync: false),
                startInfo.StandardOutputEncoding ?? new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4_096,
                leaveOpen: false);
            stdoutRead = 0;
            error = new StreamReader(
                new FileStream(new SafeFileHandle(stderrRead, ownsHandle: true), FileAccess.Read, 4_096, isAsync: false),
                startInfo.StandardErrorEncoding ?? new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4_096,
                leaveOpen: false);
            stderrRead = 0;

            CloseHandle(ref stdinRead);
            CloseHandle(ref stdoutWrite);
            CloseHandle(ref stderrWrite);
            uint resumeResult = NativeMethods.ResumeThread(threadHandle);
            if (resumeResult == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the Job-contained process.");

            return new WindowsJobProcess(process, input, output, error);
        }
        catch
        {
            if (processCreated && processHandle != 0)
                _ = NativeMethods.TerminateProcess(processHandle, 1);
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            process?.Dispose();
            throw;
        }
        finally
        {
            CloseHandle(ref stdinRead);
            CloseHandle(ref stdinWrite);
            CloseHandle(ref stdoutRead);
            CloseHandle(ref stdoutWrite);
            CloseHandle(ref stderrRead);
            CloseHandle(ref stderrWrite);
            CloseHandle(ref threadHandle);
            CloseHandle(ref processHandle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _standardInput.Dispose();
        _standardOutput.Dispose();
        _standardError.Dispose();
        Process.Dispose();
    }

    private static void CreateRedirectPipe(out nint readHandle, out nint writeHandle, bool parentReads)
    {
        var attributes = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };
        if (!NativeMethods.CreatePipe(out readHandle, out writeHandle, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a redirected process pipe.");
        nint parentHandle = parentReads ? readHandle : writeHandle;
        if (!NativeMethods.SetHandleInformation(parentHandle, NativeMethods.HANDLE_FLAG_INHERIT, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the parent pipe handle non-inheritable.");
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable)) return Path.GetFullPath(executable);
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(executable);
        string[] extensions = Path.HasExtension(executable) ? [string.Empty] : [string.Empty, ".exe"];
        foreach (string segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(segment, executable + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return executable;
    }

    private static StringBuilder BuildCommandLine(string executable, IEnumerable<string> arguments)
    {
        var result = new StringBuilder(QuoteArgument(executable));
        foreach (string argument in arguments)
            result.Append(' ').Append(QuoteArgument(argument));
        return result;
    }

    internal static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var result = new StringBuilder(argument.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static char[] BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var block = new StringBuilder();
        foreach (KeyValuePair<string, string?> pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0'))
                throw new ArgumentException("Environment variable names must be non-empty and cannot contain '=' or NUL.", nameof(environment));
            if (pair.Value?.Contains('\0') == true)
                throw new ArgumentException("Environment variable values cannot contain NUL.", nameof(environment));
            block.Append(pair.Key).Append('=').Append(pair.Value ?? string.Empty).Append('\0');
        }
        block.Append('\0');
        if (block.Length == 1) block.Append('\0');
        return block.ToString().ToCharArray();
    }

    private static void CloseHandle(ref nint handle)
    {
        if (handle == 0 || handle == new nint(-1)) return;
        _ = NativeMethods.CloseHandle(handle);
        handle = 0;
    }

    private static partial class NativeMethods
    {
        internal const uint CREATE_SUSPENDED = 0x00000004;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        internal const uint CREATE_NO_WINDOW = 0x08000000;
        internal const uint STARTF_USESTDHANDLES = 0x00000100;
        internal const uint HANDLE_FLAG_INHERIT = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out nint readPipe, out nint writePipe, ref SECURITY_ATTRIBUTES pipeAttributes, uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool CreateProcess(
            string? applicationName,
            char* commandLine,
            nint processAttributes,
            nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            char* environment,
            string? currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint ResumeThread(nint thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(nint process, uint exitCode);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SECURITY_ATTRIBUTES
        {
            internal uint nLength;
            internal nint lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFO
        {
            internal uint cb;
            internal nint lpReserved;
            internal nint lpDesktop;
            internal nint lpTitle;
            internal uint dwX;
            internal uint dwY;
            internal uint dwXSize;
            internal uint dwYSize;
            internal uint dwXCountChars;
            internal uint dwYCountChars;
            internal uint dwFillAttribute;
            internal uint dwFlags;
            internal ushort wShowWindow;
            internal ushort cbReserved2;
            internal nint lpReserved2;
            internal nint hStdInput;
            internal nint hStdOutput;
            internal nint hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            internal nint hProcess;
            internal nint hThread;
            internal uint dwProcessId;
            internal uint dwThreadId;
        }
    }
}
