using System.Diagnostics;

namespace NacosLiveAcceptance;

internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly string _logPath;
    private readonly object _logLock = new();

    private OwnedProcess(Process process, StreamWriter log, string logPath)
    {
        _process = process;
        _log = log;
        _logPath = logPath;
    }

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public static OwnedProcess Start(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        string logPath)
    {
        var fullLogPath = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullLogPath)!);
        var log = new StreamWriter(new FileStream(fullLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var ownedProcess = new OwnedProcess(process, log, fullLogPath);
        process.OutputDataReceived += (_, eventArgs) => ownedProcess.WriteLogLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => ownedProcess.WriteLogLine(eventArgs.Data);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start process: {fileName}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return ownedProcess;
        }
        catch
        {
            process.Dispose();
            log.Dispose();
            throw;
        }
    }

    public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            _process.WaitForExit();
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Process {_process.Id} did not exit within {timeout}.", exception);
        }
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
            return;
        }

        await WaitForExitAsync(timeout, cancellationToken);
    }

    public string ReadLogText()
    {
        lock (_logLock)
        {
            _log.Flush();
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        finally
        {
            _process.Dispose();
            _log.Dispose();
        }
    }

    private void WriteLogLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logLock)
        {
            _log.WriteLine(line);
        }
    }
}