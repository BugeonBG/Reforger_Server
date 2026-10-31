using System.Diagnostics;
using System.Text;

namespace RSM.Core;

/// <summary>Runs a console program and hands every line it prints to <see cref="Output"/>.
/// Events arrive on background threads.</summary>
public sealed class ProcessRunner : IDisposable
{
    private Process? _process;
    private TaskCompletionSource<int>? _exit;

    public event Action<string>? Output;

    /// <summary>Raised once, after all output has been delivered.</summary>
    public event Action<int>? Exited;

    public bool IsRunning { get; private set; }

    public int? ProcessId => IsRunning ? _process?.Id : null;

    public void Start(string exe, string args, string? workingDir = null)
    {
        if (IsRunning)
            throw new InvalidOperationException("이미 실행 중입니다.");

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "",
        };

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };

        _exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process = p;
        IsRunning = true;

        try
        {
            p.Start();
        }
        catch
        {
            IsRunning = false;
            _process = null;
            p.Dispose();
            throw;
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Watch for the end only now that the readers are running. (Listening to Process.Exited instead
        // lost the output of programs that finish instantly: the event could fire before reading began.)
        // WaitForExitAsync returns once the process has exited AND both output pipes are read to the end.
        var exit = _exit;
        _ = Task.Run(async () =>
        {
            int code;
            try
            {
                await p.WaitForExitAsync();
                code = p.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                code = -1; // disposed while shutting down
            }
            IsRunning = false;
            Exited?.Invoke(code);
            exit.TrySetResult(code);
        });
    }

    public Task<int> WaitForExitAsync() => _exit?.Task ?? Task.FromResult(0);

    /// <summary>Stops the program and everything it started.</summary>
    public void Kill()
    {
        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
        _process = null;
    }
}
