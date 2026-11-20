using ScrcpyX;

var failures = new List<string>();
void Check(bool condition, string message)
{
    if (!condition)
        failures.Add(message);
}

try
{
var root = FindRoot(AppContext.BaseDirectory);
var paths = new AppPaths(root);
paths.Verify();
var manifest = JsonStore.Load<ScrcpyManifest>(paths.Manifest, () => throw new Exception("manifest missing"), out var manifestWarning);

Check(manifestWarning is null, "manifest JSON should load without warnings");
Check(manifest.ScrcpyVersion == "4.1", "manifest version should be 4.1");
Check(manifest.Options.Count == 99, $"manifest should contain 99 options, got {manifest.Options.Count}");
Check(manifest.Groups.Count == 13, $"manifest should contain 13 groups, got {manifest.Groups.Count}");

var parsedDevices = ScrcpyTools.ParseDevices("List of devices attached\nUSB123\tdevice product:p model:Pixel_9 device:x\nTCP5555\toffline\nAUTH1\tunauthorized usb:1-2\n");
Check(parsedDevices.Count == 3, "device parser should retain ready, offline, and unauthorized devices");
Check(parsedDevices[0].Ready && parsedDevices[0].Model == "Pixel 9", "device parser should extract model and ready state");
Check(parsedDevices[1].State == "offline" && parsedDevices[2].State == "unauthorized", "device parser should preserve abnormal states");

CheckValidate(null, null, null, null);
CheckValidate("", " ", "  ", null);
CheckValidate("1920", "60", "8M", null);
CheckValidate("100", "1", "0", null);
CheckValidate("8192", "240", "8000k", null);
CheckValidate("0100", "30", "16K", null);
CheckValidate("99", "60", "8M", "最大尺寸");
CheckValidate("8193", null, null, "最大尺寸");
CheckValidate("60.0", "60", "8M", "最大尺寸");
CheckValidate("+100", "60", "8M", "最大尺寸");
CheckValidate("1920", "0", "8M", "最高帧率");
CheckValidate("1920", "241", null, "最高帧率");
CheckValidate("1920", "60.0", "8M", "最高帧率");
CheckValidate("1920", "60", "8.5M", "码率");
CheckValidate("1920", "60", "8MB", "码率");
CheckValidate("50", "0", "nope", "最大尺寸");

CheckKind("photos", true, FileIconKind.Folder);
CheckKind("photo.JPEG", false, FileIconKind.Image);
CheckKind("a.jpg", false, FileIconKind.Image);
CheckKind("a.jpeg", false, FileIconKind.Image);
CheckKind("a.png", false, FileIconKind.Image);
CheckKind("a.gif", false, FileIconKind.Image);
CheckKind("a.webp", false, FileIconKind.Image);
CheckKind("a.HEIC", false, FileIconKind.Image);
CheckKind("a.bmp", false, FileIconKind.Image);
CheckKind("clip.mp4", false, FileIconKind.Video);
CheckKind("clip.mkv", false, FileIconKind.Video);
CheckKind("clip.MOV", false, FileIconKind.Video);
CheckKind("clip.avi", false, FileIconKind.Video);
CheckKind("clip.webm", false, FileIconKind.Video);
CheckKind("song.mp3", false, FileIconKind.Audio);
CheckKind("song.m4a", false, FileIconKind.Audio);
CheckKind("song.wav", false, FileIconKind.Audio);
CheckKind("song.FLAC", false, FileIconKind.Audio);
CheckKind("song.ogg", false, FileIconKind.Audio);
CheckKind("pack.zip", false, FileIconKind.Archive);
CheckKind("pack.rar", false, FileIconKind.Archive);
CheckKind("pack.7z", false, FileIconKind.Archive);
CheckKind("pack.tar", false, FileIconKind.Archive);
CheckKind("pack.TAR.GZ", false, FileIconKind.Archive);
CheckKind("app.APK", false, FileIconKind.Apk);
CheckKind("note.txt", false, FileIconKind.Text);
CheckKind("note.md", false, FileIconKind.Text);
CheckKind("note.log", false, FileIconKind.Text);
CheckKind("note.json", false, FileIconKind.Text);
CheckKind("note.xml", false, FileIconKind.Text);
CheckKind("note.csv", false, FileIconKind.Text);
CheckKind("data.bin", false, FileIconKind.File);
CheckKind("readme", false, FileIconKind.File);
CheckKind(null, false, FileIconKind.File);
Check((int)FileKind.Glyph(FileIconKind.Folder)[0] == 0xED25, "folder glyph");
Check((int)FileKind.Glyph(FileIconKind.Image)[0] == 0xE8B9, "image glyph");
Check((int)FileKind.Glyph(FileIconKind.Video)[0] == 0xE714, "video glyph");
Check((int)FileKind.Glyph(FileIconKind.Audio)[0] == 0xE8D6, "audio glyph");
Check((int)FileKind.Glyph(FileIconKind.Archive)[0] == 0xE7B8, "archive glyph");
Check((int)FileKind.Glyph(FileIconKind.Apk)[0] == 0xE74C, "apk glyph");
Check((int)FileKind.Glyph(FileIconKind.Text)[0] == 0xE9F9, "text glyph");
Check((int)FileKind.Glyph(FileIconKind.File)[0] == 0xE8A5, "file glyph");
CheckSize(0, "0 B");
CheckSize(512, "512 B");
CheckSize(1023, "1023 B");
CheckSize(1024, "1.0 KB");
CheckSize(1536, "1.5 KB");
CheckSize(1048576, "1.0 MB");
CheckSize(3221225472, "3.00 GB");
Check(new DeviceEntry("dir", "/dir", true, 4096, 0).SizeLabel == "", "directory size should be blank");
Check(TransferProgress.Percent(0, 100) == 0, "transfer 0 of 100");
Check(TransferProgress.Percent(50, 100) == 50, "transfer 50 of 100");
Check(TransferProgress.Percent(100, 100) == 100, "transfer complete");
Check(TransferProgress.Percent(150, 100) == 100, "transfer clamps overshoot");
Check(TransferProgress.Percent(1, 0) is null, "transfer rejects zero total");
Check(TransferProgress.Percent(-1, 10) is null, "transfer rejects negative done");
Check(TransferProgress.ParseBytes("4096\n") == 4096, "stat bytes");
Check(TransferProgress.ParseBytes("nope") is null, "stat rejects text");
Check(TransferProgress.ParseBytes(null) is null, "stat rejects empty");
var rolling = new RollingAverage(3);
Check(rolling.Add(10) == 10, "rolling first");
Check(rolling.Add(20) == 15, "rolling second");
Check(rolling.Add(30) == 20, "rolling third");
Check(rolling.Add(40) == 30, "rolling drops the oldest sample");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAILED ({failures.Count})");
    foreach (var failure in failures)
        Console.Error.WriteLine(" - " + failure);
    return 1;
}

Console.WriteLine($"PASS: scrcpy {manifest.ScrcpyVersion}, {manifest.Options.Count} options, {manifest.Groups.Count} groups, device/input checks");
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAILED (exception)");
    Console.Error.WriteLine(ex);
    return 1;
}

void CheckValidate(string? maxSize, string? maxFps, string? bitRate, string? expected)
{
    var actual = MirrorInput.Validate(maxSize, maxFps, bitRate);
    var shown = $"size={Show(maxSize)} fps={Show(maxFps)} rate={Show(bitRate)}";
    if (expected is null)
        Check(actual is null, $"valid input should pass: {shown}, got {actual}");
    else
        Check(actual?.Contains(expected) == true, $"invalid input should mention {expected}: {shown}, got {actual}");
}

void CheckSize(long bytes, string expected)
{
    var actual = FileSize.Format(bytes);
    Check(actual == expected, $"size {bytes} should be {expected}, got {actual}");
    var label = new DeviceEntry("f", "/f", false, bytes, 0).SizeLabel;
    Check(label == expected, $"SizeLabel {bytes} should be {expected}, got {label}");
}

void CheckKind(string? name, bool isDirectory, FileIconKind expected)
{
    var actual = FileKind.Classify(name, isDirectory);
    Check(actual == expected, $"kind {Show(name)} dir={isDirectory} should be {expected}, got {actual}");
}

static string Show(string? value) => value is null ? "<null>" : "'" + value + "'";

static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DLL", "adb.exe"))
            && File.Exists(Path.Combine(directory.FullName, "DLL", "scrcpy-options.json")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("Could not locate repository root.");
}
