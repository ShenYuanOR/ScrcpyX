using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ScrcpyX;

public sealed record DeviceEntry(string Name, string FullPath, bool IsDirectory, long Size, long UnixSeconds)
{
    public string TypeLabel => IsDirectory ? "文件夹" : "文件";
    public string SizeLabel => IsDirectory ? "" : FileSize.Format(Size);
    public string IconGlyph => FileKind.Glyph(FileKind.Classify(Name, IsDirectory));
    public string DetailLabel =>
        string.IsNullOrEmpty(SizeLabel) ? WhenLabel :
        string.IsNullOrEmpty(WhenLabel) ? SizeLabel :
        SizeLabel + " · " + WhenLabel;
    public string WhenLabel => UnixSeconds <= 0
        ? ""
        : DateTimeOffset.FromUnixTimeSeconds(UnixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

public static class FileSize
{
    public static string Format(long bytes)
    {
        const long kb = 1024;
        const long mb = 1024 * 1024;
        const long gb = 1024L * 1024 * 1024;
        if (bytes < kb)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < mb)
            return (bytes / (double)kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < gb)
            return (bytes / (double)mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (double)gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }
}

public sealed class DeviceFiles(AppPaths paths)
{
    public async Task<IReadOnlyList<DeviceEntry>> ListAsync(string serial, string directory)
    {
        var script = "p=$(printf %s \"$1\" | toybox base64 -d); toybox find -H \"$p\" -maxdepth 1 -print0 | toybox xargs -0 toybox stat -c \"%F\t%s\t%Y\t%n\u001e\"";
        var text = await ExecOutAsync(serial, script, Convert.ToBase64String(Encoding.UTF8.GetBytes(directory)));
        var entries = new List<DeviceEntry>();
        foreach (var record in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = record.Trim('\r', '\n', '\0', ' ');
            if (line.Length == 0)
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;
            var full = string.Join('\t', parts.Skip(3)).TrimEnd('\0', '\r', '\n');
            if (full.Length == 0 || string.Equals(PathTrim(full), PathTrim(directory), StringComparison.Ordinal))
                continue;
            var directoryKind = parts[0].Contains("directory", StringComparison.OrdinalIgnoreCase);
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds);
            entries.Add(new DeviceEntry(Path.GetFileName(full.Replace('/', Path.DirectorySeparatorChar)), full, directoryKind, size, seconds));
        }
        return entries
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task PullAsync(string serial, string remote, string local, IProgress<int>? percent = null, CancellationToken cancellationToken = default)
    {
        long? total = null;
        if (percent is not null)
            total = await StatBytesAsync(serial, remote, cancellationToken);
        await RunPolledAsync(serial, ["pull", remote, local], cancellationToken, () =>
        {
            if (percent is null || total is not > 0 || Directory.Exists(local))
                return Task.CompletedTask;
            var done = File.Exists(local) ? new FileInfo(local).Length : 0;
            var value = TransferProgress.Percent(done, total.Value);
            if (value is int shown)
                percent.Report(shown);
            return Task.CompletedTask;
        });
    }

    public async Task PushAsync(string serial, string local, string remote, IProgress<int>? percent = null, CancellationToken cancellationToken = default)
    {
        var total = File.Exists(local) ? new FileInfo(local).Length : 0;
        await RunPolledAsync(serial, ["push", local, remote], cancellationToken, async () =>
        {
            if (percent is null || total <= 0)
                return;
            var done = await StatBytesAsync(serial, remote, cancellationToken);
            if (done is not long size)
                return;
            var value = TransferProgress.Percent(size, total);
            if (value is int shown)
                percent.Report(shown);
        });
    }

    public async Task<long?> StatBytesAsync(string serial, string remote, CancellationToken cancellationToken = default)
    {
        var script = "p=$(printf %s \"$1\" | toybox base64 -d); toybox stat -c %s -- \"$p\"";
        var (code, stdout, _) = await CaptureAsync(
            Remote(serial, script, Convert.ToBase64String(Encoding.UTF8.GetBytes(remote))),
            binary: false,
            onLine: null,
            cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("已取消。", cancellationToken);
        if (code != 0)
            return null;
        return TransferProgress.ParseBytes(stdout);
    }

    public Task DeleteAsync(string serial, string remote, CancellationToken cancellationToken = default) =>
        ShellPathAsync(serial, "p=$(printf %s \"$1\" | toybox base64 -d); toybox rm -r -- \"$p\"", remote, cancellationToken);

    public Task MkdirAsync(string serial, string remote) =>
        ShellPathAsync(serial, "p=$(printf %s \"$1\" | toybox base64 -d); toybox mkdir -- \"$p\"", remote, CancellationToken.None);

    public Task RenameAsync(string serial, string source, string destination) =>
        MoveAsync(serial, source, destination);

    public Task MoveAsync(string serial, string source, string destination, CancellationToken cancellationToken = default) =>
        ShellPairAsync(serial, "toybox mv --", source, destination, cancellationToken);

    public Task CopyAsync(string serial, string source, string destination, CancellationToken cancellationToken = default) =>
        ShellPairAsync(serial, "toybox cp -r --", source, destination, cancellationToken);

    public async Task<bool> ExistsAsync(string serial, string remote, CancellationToken cancellationToken = default)
    {
        var script = "p=$(printf %s \"$1\" | toybox base64 -d); toybox test -e \"$p\"; echo $?";
        var (_, stdout, _) = await CaptureAsync(
            Remote(serial, script, Convert.ToBase64String(Encoding.UTF8.GetBytes(remote))),
            binary: false,
            onLine: null,
            cancellationToken);
        var line = stdout.Replace("\r", "").Trim().Split('\n').LastOrDefault()?.Trim();
        return line == "0";
    }

    public async Task<double?> EchoMillisecondsAsync(string serial, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var (code, stdout, _) = await CaptureAsync(
            ["-s", serial, "shell", "echo", "scrcpyx"],
            binary: false,
            onLine: null,
            cancellationToken);
        if (code != 0 || !stdout.Contains("scrcpyx", StringComparison.Ordinal))
            return null;
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private Task ShellPathAsync(string serial, string script, string path, CancellationToken cancellationToken) =>
        ExecAsync(serial, script, cancellationToken, Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));

    private Task ShellPairAsync(string serial, string command, string source, string destination, CancellationToken cancellationToken)
    {
        var script = "a=$(printf %s \"$1\" | toybox base64 -d); b=$(printf %s \"$2\" | toybox base64 -d); " + command + " \"$a\" \"$b\"";
        return ExecAsync(
            serial,
            script,
            cancellationToken,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(source)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(destination)));
    }

    private async Task<string> ExecOutAsync(string serial, string script, params string[] base64Args)
    {
        var (code, stdout, stderr) = await CaptureAsync(Remote(serial, script, base64Args), binary: true, onLine: null, CancellationToken.None);
        if (code != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "读取手机目录失败。" : stderr.Trim());
        return stdout;
    }

    private async Task ExecAsync(string serial, string script, CancellationToken cancellationToken, params string[] base64Args)
    {
        var (code, _, stderr) = await CaptureAsync(Remote(serial, script, base64Args), binary: false, onLine: null, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("已取消。", cancellationToken);
        if (code != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "手机上的文件操作失败。" : stderr.Trim());
    }

    private static List<string> Remote(string serial, string script, params string[] base64Args) =>
        ["-s", serial, "exec-out", "sh -c '" + script + "' sh " + string.Join(' ', base64Args)];

    private async Task RunPolledAsync(string serial, IReadOnlyList<string> adbArgs, CancellationToken cancellationToken, Func<Task> poll)
    {
        var args = new List<string> { "-s", serial };
        args.AddRange(adbArgs);
        var start = new ProcessStartInfo(paths.Adb)
        {
            WorkingDirectory = paths.DllDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 adb。");
        using var registration = cancellationToken.Register(() => StopProcess(process));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await poll().ConfigureAwait(false);
                try
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
            await poll().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            await DrainAsync(stdout, stderr).ConfigureAwait(false);
            throw new OperationCanceledException("已取消。", cancellationToken);
        }
        var error = await stderr.ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("已取消。", cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "adb 失败，退出码 " + process.ExitCode : error.Trim());
    }

    private async Task<(int Code, string Stdout, string Stderr)> CaptureAsync(
        IReadOnlyList<string> args,
        bool binary,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(paths.Adb)
        {
            WorkingDirectory = paths.DllDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = binary ? Encoding.UTF8 : Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 adb。");
        using var registration = cancellationToken.Register(() => StopProcess(process));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = ReadTextAsync(process.StandardError, onLine);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            await DrainAsync(stdout, stderr).ConfigureAwait(false);
            throw new OperationCanceledException("已取消。", cancellationToken);
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadTextAsync(StreamReader reader, Action<string>? onLine)
    {
        var all = new StringBuilder();
        var pending = new StringBuilder();
        var buffer = new char[512];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
                break;
            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                if (ch is '\r' or '\n')
                {
                    if (pending.Length == 0)
                        continue;
                    var line = pending.ToString();
                    pending.Clear();
                    all.AppendLine(line);
                    onLine?.Invoke(line);
                }
                else
                {
                    pending.Append(ch);
                }
            }
        }
        if (pending.Length > 0)
        {
            var line = pending.ToString();
            all.Append(line);
            onLine?.Invoke(line);
        }
        return all.ToString();
    }

    private static async Task DrainAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    private static string PathTrim(string path) => path.TrimEnd('/', '\\');
}

public static class TransferProgress
{
    public static int? Percent(long done, long total)
    {
        if (total <= 0 || done < 0)
            return null;
        if (done >= total)
            return 100;
        return (int)(done * 100 / total);
    }

    public static long? ParseBytes(string? text)
    {
        var line = text?.Replace("\r", "").Trim().Split('\n').LastOrDefault()?.Trim();
        if (long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= 0)
            return size;
        return null;
    }
}

public sealed class RollingAverage(int window)
{
    private readonly double[] _samples = new double[window];
    private int _count;
    private int _next;
    private double _sum;

    public double Add(double value)
    {
        if (_count < _samples.Length)
        {
            _samples[_count++] = value;
            _sum += value;
        }
        else
        {
            _sum -= _samples[_next];
            _samples[_next] = value;
            _sum += value;
            _next = (_next + 1) % _samples.Length;
        }
        return _sum / _count;
    }

    public void Reset()
    {
        Array.Clear(_samples);
        _count = 0;
        _next = 0;
        _sum = 0;
    }
}
