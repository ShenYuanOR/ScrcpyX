using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace ScrcpyX;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var paths = new AppPaths(AppContext.BaseDirectory);
        try
        {
            paths.Verify();
            var manifest = JsonStore.Load<ScrcpyManifest>(paths.Manifest, () => throw new InvalidDataException("参数清单为空。"), out var warning);
            if (manifest.Options.Count != 99)
                throw new InvalidDataException($"参数清单应包含 99 个 Windows 可调节参数，实际为 {manifest.Options.Count} 个。");

            _window = new ShellWindow(paths, manifest, warning);
            _window.Activate();
        }
        catch (Exception ex)
        {
            NativeDialog.Error(ex.Message, "ScrcpyX 无法启动");
            Exit();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        NativeDialog.Error(e.Message, "ScrcpyX");
        e.Handled = true;
    }
}

internal static class RuntimeGate
{
    private static bool _runtimeReady;

    [ModuleInitializer]
    internal static void Check()
    {
        try
        {
            if (!HasDotNet10Desktop())
            {
                OpenDownload("https://dotnet.microsoft.com/download/dotnet/10.0");
                NativeDialog.Error(
                    "缺少 .NET 10，ScrcpyX 无法启动。\n\n请下载安装：\nhttps://dotnet.microsoft.com/download/dotnet/10.0",
                    "ScrcpyX 无法启动");
                Environment.Exit(1);
            }

            var minVersion = new PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64);
            if (!Bootstrap.TryInitialize(
                    Microsoft.WindowsAppSDK.Release.MajorMinor,
                    Microsoft.WindowsAppSDK.Release.VersionTag,
                    minVersion,
                    Bootstrap.InitializeOptions.None,
                    out _))
            {
                OpenDownload("https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads");
                NativeDialog.Error(
                    "缺少 Windows App Runtime 1.8，ScrcpyX 无法启动。\n\n请下载安装：\nhttps://learn.microsoft.com/windows/apps/windows-app-sdk/downloads",
                    "ScrcpyX 无法启动");
                Environment.Exit(2);
            }

            _runtimeReady = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
        catch (Exception ex)
        {
            try
            {
                NativeDialog.Error("启动预检没有完成。\n" + ex.Message, "ScrcpyX 无法启动");
            }
            catch (Exception)
            {
            }
            Environment.Exit(3);
        }
    }

    private static void OpenDownload(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private static void Shutdown()
    {
        if (!_runtimeReady)
            return;
        _runtimeReady = false;
        try
        {
            Bootstrap.Shutdown();
        }
        catch (Exception)
        {
        }
    }

    private static bool HasDotNet10Desktop()
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET 10.", StringComparison.Ordinal))
                return true;
            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App")
            };
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot))
                roots.Add(Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App"));
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (Path.GetFileName(dir).StartsWith("10.", StringComparison.Ordinal))
                        return true;
                }
            }
        }
        catch (Exception)
        {
        }
        return false;
    }
}

internal static class NativeDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    public static void Error(string text, string caption) => MessageBox(0, text, caption, 0x10);
}
