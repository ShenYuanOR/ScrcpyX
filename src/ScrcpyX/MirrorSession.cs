using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ScrcpyX;

public sealed class MirrorSession : IAsyncDisposable
{
    public const int KeyBack = 4;
    public const int KeyHome = 3;
    public const int KeyRecents = 187;

    private readonly AppPaths _paths;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private readonly ConcurrentQueue<byte[]> _commands = new();
    private readonly SemaphoreSlim _wake = new(0);
    private byte[]? _move;
    private Process? _server;
    private TcpClient? _video;
    private TcpClient? _control;
    private int _port;
    private string? _serial;
    private int _frameWidth;
    private int _frameHeight;
    private byte[]? _config;
    private int _failedOnce;

    public event Action<int, int>? FrameSizeChanged;
    public event Action<string>? Failed;

    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;

    public MirrorSession(AppPaths paths) => _paths = paths;

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _failedOnce, 1) != 0)
            return;
        try
        {
            Failed?.Invoke(message);
        }
        catch (Exception)
        {
        }
        _cancel.Cancel();
    }

    public async Task RunAsync(string serial, MirrorOptions options)
    {
        _serial = serial;
        var scid = Random.Shared.Next(1, int.MaxValue).ToString("x8", CultureInfo.InvariantCulture);
        _port = FreePort();
        var socket = "scrcpy_" + scid;
        await AdbAsync(["-s", serial, "push", _paths.Server, "/data/local/tmp/scrcpy-server.jar"]);
        await AdbAsync(["-s", serial, "forward", "tcp:" + _port, "localabstract:" + socket]);

        var args = new List<string>
        {
            "-s", serial, "shell",
            "CLASSPATH=/data/local/tmp/scrcpy-server.jar",
            "app_process", "/", "com.genymobile.scrcpy.Server", "4.1",
            "scid=" + scid,
            "audio=false",
            "tunnel_forward=true"
        };
        if (options.MaxSize is > 0)
            args.Add("max_size=" + options.MaxSize.Value.ToString(CultureInfo.InvariantCulture));
        var maxFps = options.MaxFps is > 0 ? options.MaxFps.Value : 60;
        args.Add("max_fps=" + maxFps.ToString(CultureInfo.InvariantCulture));
        args.Add("video_bit_rate=" + (string.IsNullOrWhiteSpace(options.VideoBitRate) ? "32000000" : BitsPerSecond(options.VideoBitRate)));
        if (options.StayAwake)
            args.Add("stay_awake=true");

        _server = StartProcess(_paths.Adb, args);
        var log = new StringBuilder();
        _ = PumpLogAsync(_server.StandardOutput, log);
        _ = PumpLogAsync(_server.StandardError, log);
        try
        {
            TcpClient? video = null;
            Stream? videoStream = null;
            var dummy = new byte[1];
            for (var attempt = 0; ; attempt++)
            {
                video?.Close();
                video = await ConnectAsync();
                videoStream = video.GetStream();
                try
                {
                    await ReadExactAsync(videoStream, dummy);
                    break;
                }
                catch (EndOfStreamException) when (attempt < 40)
                {
                    await Task.Delay(100, _cancel.Token);
                }
            }
            _video = video;
            _control = await ConnectAsync();
            _ = DrainControlAsync(_control.GetStream());
            _ = ControlWriterAsync();
            if (options.TurnScreenOff)
                SendPower(false);
            await ReadVideoAsync(videoStream, log, skipDummy: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_cancel.IsCancellationRequested)
                return;
            await Task.Delay(200);
            if (_cancel.IsCancellationRequested)
                return;
            string tail;
            lock (log)
                tail = log.Length == 0 ? ex.Message : ex.Message + "\n" + log;
            Fail(tail.Trim());
        }
    }

    public void Key(int keyCode)
    {
        _commands.Enqueue(KeyPacket(0, keyCode));
        _commands.Enqueue(KeyPacket(1, keyCode));
        _wake.Release();
    }

    public void Touch(int action, int x, int y)
    {
        if (_frameWidth <= 0 || _frameHeight <= 0)
            return;
        var packet = new byte[32];
        packet[0] = 2;
        packet[1] = (byte)action;
        for (var i = 2; i < 10; i++)
            packet[i] = 0xFF;
        WriteBe(packet, 10, x);
        WriteBe(packet, 14, y);
        packet[18] = (byte)(_frameWidth >> 8);
        packet[19] = (byte)_frameWidth;
        packet[20] = (byte)(_frameHeight >> 8);
        packet[21] = (byte)_frameHeight;
        packet[22] = 0xFF;
        packet[23] = 0xFF;
        var button = action == 2 ? 0 : 1;
        WriteBe(packet, 24, button);
        WriteBe(packet, 28, action == 1 ? 0 : 1);
        if (action == 2)
        {
            lock (_gate)
                _move = packet;
        }
        else
        {
            _commands.Enqueue(packet);
        }
        _wake.Release();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _cancel.Cancel();
        Close(_video);
        Close(_control);
        try
        {
            if (_server is { HasExited: false })
                _server.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        if (_serial is not null && _port > 0)
        {
            try
            {
                await AdbAsync(["-s", _serial, "forward", "--remove", "tcp:" + _port]);
            }
            catch
            {
            }
        }
    }

    private async Task ReadVideoAsync(Stream stream, StringBuilder log, bool skipDummy)
    {
        if (!skipDummy)
        {
            var one = new byte[1];
            await ReadExactAsync(stream, one);
        }
        var name = new byte[64];
        await ReadExactAsync(stream, name);
        var codecBytes = new byte[4];
        await ReadExactAsync(stream, codecBytes);
        var codec = BinaryPrimitives(codecBytes);
        if (codec != 0x68323634)
            throw new InvalidDataException("手机给出的不是 H.264（" + codec.ToString("x8", CultureInfo.InvariantCulture) + "）。" + Tail(log));

        var header = new byte[12];
        while (!_cancel.IsCancellationRequested)
        {
            await ReadExactAsync(stream, header.AsMemory(0, 1));
            if ((header[0] & 0x80) != 0)
            {
                await ReadExactAsync(stream, header.AsMemory(1, 11));
                var width = ReadBe(header, 4);
                var height = ReadBe(header, 8);
                if (width > 0 && height > 0 && (width != _frameWidth || height != _frameHeight))
                {
                    _frameWidth = width;
                    _frameHeight = height;
                    FrameSizeChanged?.Invoke(width, height);
                }
                continue;
            }

            await ReadExactAsync(stream, header.AsMemory(1, 11));
            var raw = ReadBe64(header, 0);
            var config = (raw & (1UL << 62)) != 0;
            var key = (raw & (1UL << 61)) != 0;
            var pts = raw & ((1UL << 61) - 1);
            var size = ReadBe(header, 8);
            if (size <= 0 || size > 8_000_000)
                throw new InvalidDataException("视频包长度异常：" + size);
            var payload = new byte[size];
            await ReadExactAsync(stream, payload);
            if (config)
            {
                _config = payload;
                continue;
            }
            byte[] frame = payload;
            if (key && _config is { Length: > 0 })
            {
                frame = new byte[_config.Length + payload.Length];
                _config.CopyTo(frame, 0);
                payload.CopyTo(frame, _config.Length);
            }
            FrameReceived?.Invoke(new VideoFrame(frame, pts, key));
        }
    }

    public event Action<VideoFrame>? FrameReceived;

    private async Task DrainControlAsync(Stream stream)
    {
        var header = new byte[9];
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                await ReadExactAsync(stream, header.AsMemory(0, 1));
                var type = header[0];
                if (type == 0)
                {
                    await ReadExactAsync(stream, header.AsMemory(0, 4));
                    var length = ReadBe(header, 0);
                    if (length < 0 || length > 1_000_000)
                        return;
                    if (length > 0)
                        await ReadExactAsync(stream, new byte[length]);
                }
                else if (type == 1)
                {
                    await ReadExactAsync(stream, header.AsMemory(0, 8));
                }
                else if (type == 2)
                {
                    await ReadExactAsync(stream, header.AsMemory(0, 4));
                    var size = (header[2] << 8) | header[3];
                    if (size > 0)
                        await ReadExactAsync(stream, new byte[size]);
                }
                else
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
        }
    }

    private void SendPower(bool on)
    {
        _commands.Enqueue([10, on ? (byte)1 : (byte)0]);
        _wake.Release();
    }

    private async Task ControlWriterAsync()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                await _wake.WaitAsync(_cancel.Token);
                while (_commands.TryDequeue(out var command))
                {
                    if (!WriteControl(command))
                        return;
                }
                byte[]? move;
                lock (_gate)
                {
                    move = _move;
                    _move = null;
                }
                if (move is not null && !WriteControl(move))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool WriteControl(byte[] packet)
    {
        try
        {
            _control?.GetStream().Write(packet);
            return true;
        }
        catch (Exception ex)
        {
            if (!_cancel.IsCancellationRequested)
                Fail("控制连接已断开：" + ex.Message);
            return false;
        }
    }

    private static byte[] KeyPacket(byte action, int keyCode)
    {
        var packet = new byte[14];
        packet[0] = 0;
        packet[1] = action;
        WriteBe(packet, 2, keyCode);
        return packet;
    }

    private async Task<TcpClient> ConnectAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            _cancel.Token.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync("127.0.0.1", _port, timeout.Token);
                return client;
            }
            catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
            {
                client.Dispose();
                await Task.Delay(100, _cancel.Token);
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(100, _cancel.Token);
            }
        }
        throw new TimeoutException("连不上手机上的投屏服务。");
    }

    private async Task ReadExactAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], _cancel.Token);
            if (read == 0)
                throw new EndOfStreamException("画面连接已断开。");
            offset += read;
        }
    }

    private async Task AdbAsync(IReadOnlyList<string> args)
    {
        var process = StartProcess(_paths.Adb, args);
        var stdout = process.StandardOutput.ReadToEndAsync(_cancel.Token);
        var stderr = process.StandardError.ReadToEndAsync(_cancel.Token);
        await process.WaitForExitAsync(_cancel.Token);
        var error = (await stderr).Trim();
        _ = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Length == 0 ? "adb 失败，退出码 " + process.ExitCode : error);
    }

    private Process StartProcess(string file, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = _paths.DllDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动 " + Path.GetFileName(file));
    }

    private async Task PumpLogAsync(StreamReader reader, StringBuilder log)
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cancel.Token);
                if (line is null)
                    break;
                lock (log)
                {
                    if (log.Length < 4000)
                        log.AppendLine(line);
                }
            }
        }
        catch
        {
        }
    }

    private static string Tail(StringBuilder log)
    {
        lock (log)
            return log.Length == 0 ? "" : "\n" + log;
    }

    private static string BitsPerSecond(string text)
    {
        text = text.Trim();
        long multiplier = 1;
        if (text.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000_000;
            text = text[..^1];
        }
        else if (text.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000;
            text = text[..^1];
        }
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? (value * multiplier).ToString(CultureInfo.InvariantCulture)
            : text;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Close(TcpClient? client)
    {
        try
        {
            client?.Close();
        }
        catch
        {
        }
    }

    private static int BinaryPrimitives(byte[] bytes) => ReadBe(bytes, 0);

    private static int ReadBe(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static ulong ReadBe64(byte[] bytes, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | bytes[offset + i];
        return value;
    }

    private static void WriteBe(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }
}

public sealed record MirrorOptions(int? MaxSize, int? MaxFps, string? VideoBitRate, bool StayAwake, bool TurnScreenOff);

public sealed record VideoFrame(byte[] Data, ulong Pts, bool Key);

public static partial class MirrorInput
{
    public static string? Validate(string? maxSize, string? maxFps, string? bitRate)
    {
        var sizeError = CheckInteger(maxSize, 100, 8192, "最大尺寸必须是 100 到 8192 的整数。");
        if (sizeError is not null)
            return sizeError;
        var fpsError = CheckInteger(maxFps, 1, 240, "最高帧率必须是 1 到 240 的整数。");
        if (fpsError is not null)
            return fpsError;
        if (string.IsNullOrWhiteSpace(bitRate))
            return null;
        return BitRate().IsMatch(bitRate.Trim()) ? null : "码率必须是数字，可选后缀 K 或 M。";
    }

    private static string? CheckInteger(string? text, int min, int max, string error)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max
            ? null
            : error;
    }

    [GeneratedRegex(@"^\d+[MmKk]?$", RegexOptions.CultureInvariant)]
    private static partial Regex BitRate();
}
