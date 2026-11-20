using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScrcpyX;

public sealed class AppPaths
{
    public AppPaths(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        DllDirectory = Path.Combine(BaseDirectory, "DLL");
        DataDirectory = Path.Combine(BaseDirectory, "data");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        Adb = Path.Combine(DllDirectory, "adb.exe");
        Server = Path.Combine(DllDirectory, "scrcpy-server");
        Manifest = Path.Combine(DllDirectory, "scrcpy-options.json");
        Settings = Path.Combine(DataDirectory, "settings.json");
        WindowState = Path.Combine(DataDirectory, "window-state.json");
    }

    public string BaseDirectory { get; }
    public string DllDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string Adb { get; }
    public string Server { get; }
    public string Manifest { get; }
    public string Settings { get; }
    public string WindowState { get; }

    public void Verify()
    {
        foreach (var path in new[] { Adb, Server, Manifest })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"运行文件缺失：{Path.GetRelativePath(BaseDirectory, path)}", path);
        }

        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var probe = Path.Combine(DataDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok", Encoding.UTF8);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new IOException("data 目录不可写。请将 ScrcpyX 移到普通用户可写目录后再启动，配置不会改写到其他位置。", ex);
        }
    }
}

public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T Load<T>(string path, Func<T> fallback, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
            return fallback();

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Options) ?? fallback();
        }
        catch (Exception ex)
        {
            warning = $"无法读取 {Path.GetFileName(path)}：{ex.Message}";
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

public sealed class ScrcpyManifest
{
    public string ScrcpyVersion { get; set; } = "";
    public string GeneratedFrom { get; set; } = "";
    public List<OptionGroup> Groups { get; set; } = [];
    public List<OptionDefinition> Options { get; set; } = [];
}

public sealed class OptionGroup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int Order { get; set; }
    public override string ToString() => Title;
}

public sealed class OptionDefinition
{
    public string Argument { get; set; } = "";
    public string ChineseTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public string Group { get; set; } = "";
    public string Section { get; set; } = "";
    public int Order { get; set; }
    public string Control { get; set; } = "text";
    public bool OptionalValue { get; set; }
    public string ValueFormat { get; set; } = "";
    public string DefaultState { get; set; } = "scrcpy-default";
    public List<string> AllowedValues { get; set; } = [];
    public List<string> EnabledWhen { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
    public string Platform { get; set; } = "windows";
    public string Help { get; set; } = "";
    public string OfficialHelp { get; set; } = "";
    public string Example { get; set; } = "";
    public string Documentation { get; set; } = "";
    public bool Risky { get; set; }
}

public sealed class OptionState
{
    public bool Enabled { get; set; }
    public string Value { get; set; } = "";

    public OptionState Clone() => new() { Enabled = Enabled, Value = Value };
}

public sealed class UserSettings
{
    public Dictionary<string, OptionState> Draft { get; set; } = [];
}

public sealed class SavedWindowState
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed record DeviceInfo(string Serial, string State, string Model, string Details)
{
    public bool Ready => State == "device";
    public bool Unauthorized => State.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    public bool Offline => State.Contains("offline", StringComparison.OrdinalIgnoreCase);
    public string ShortName => string.IsNullOrWhiteSpace(Model) ? Serial : Model;
    public string DisplayName => string.IsNullOrWhiteSpace(Model)
        ? $"{Serial} · {State}"
        : $"{Model} ({Serial}) · {State}";
    public string FriendlyName
    {
        get
        {
            if (Unauthorized)
                return $"{ShortName} · 等待手机授权";
            if (Offline)
                return $"{ShortName} · 已断开";
            if (Ready)
                return ShortName;
            return $"{ShortName} · {State}";
        }
    }
    public override string ToString() => FriendlyName;
}

public sealed class ScrcpyTools(AppPaths paths)
{
    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, error) = await RunAsync(paths.Adb, ["devices", "-l"], cancellationToken);
        var devices = new List<DeviceInfo>();
        return ParseDevices(output + "\n" + error);
    }

    public static List<DeviceInfo> ParseDevices(string text)
    {
        var devices = new List<DeviceInfo>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*'))
                continue;
            var parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2)
                continue;
            var model = parts.Skip(2).FirstOrDefault(x => x.StartsWith("model:"))?[6..].Replace('_', ' ') ?? "";
            devices.Add(new(parts[0], parts[1], model, string.Join(' ', parts.Skip(2))));
        }
        return devices;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = paths.DllDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["ADB"] = paths.Adb;
        start.Environment["SCRCPY_SERVER_PATH"] = paths.Server;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
