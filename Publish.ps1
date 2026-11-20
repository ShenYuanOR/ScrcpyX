param([string]$Version = '0.4.0')

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'publish'))
$target = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'publish\ScrcpyX'))
$publishPrefix = $publishRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a path outside publish.'
}

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$toolDotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (Test-Path $toolDotnet) {
    $dotnet = $toolDotnet
} elseif (Test-Path $userDotnet) {
    $dotnet = $userDotnet
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH

& $dotnet build (Join-Path $repoRoot 'ScrcpyX.sln') -c Release -p:Platform=x64
if ($LASTEXITCODE) { throw 'Build failed.' }
& $dotnet run --project (Join-Path $repoRoot 'tests\ScrcpyX.SelfTest\ScrcpyX.SelfTest.csproj') -c Release --no-build
if ($LASTEXITCODE) { throw 'Self-test failed.' }

if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $target | Out-Null
& $dotnet publish (Join-Path $repoRoot 'src\ScrcpyX\ScrcpyX.csproj') -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o $target
if ($LASTEXITCODE) { throw 'Publish failed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $target 'data\logs') | Out-Null
Get-ChildItem -LiteralPath $target -Recurse -File -Filter *.pdb | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $target 'ScrcpyX.exe') -Destination (Join-Path $target 'ScrcpyX.runtime.exe') -Force
Copy-Item -LiteralPath (Join-Path $target 'ScrcpyX.runtimeconfig.json') -Destination (Join-Path $target 'ScrcpyX.runtime.runtimeconfig.json') -Force
Copy-Item -LiteralPath (Join-Path $target 'ScrcpyX.deps.json') -Destination (Join-Path $target 'ScrcpyX.runtime.deps.json') -Force
Copy-Item -LiteralPath (Join-Path $target 'ScrcpyX.pri') -Destination (Join-Path $target 'ScrcpyX.runtime.pri') -Force
$launcher = Join-Path $env:TEMP 'scrcpyx-framework-launch.cs'
@'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static int Main()
    {
        try
        {
            if (!HasDotNet10Desktop())
            {
                OpenDownload("https://dotnet.microsoft.com/download/dotnet/10.0");
                MessageBox(IntPtr.Zero, "\u7f3a\u5c11 .NET 10\uff0cScrcpyX \u65e0\u6cd5\u542f\u52a8\u3002\r\n\r\n\u8bf7\u4e0b\u8f7d\u5b89\u88c5\uff1a\r\nhttps://dotnet.microsoft.com/download/dotnet/10.0", "ScrcpyX \u65e0\u6cd5\u542f\u52a8", 0x10);
                return 1;
            }

            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var app = Path.Combine(dir, "ScrcpyX.runtime.exe");
            if (!File.Exists(app))
            {
                MessageBox(IntPtr.Zero, "\u7f3a\u5c11 .NET 10\uff0cScrcpyX \u65e0\u6cd5\u542f\u52a8\u3002\r\n\r\n\u8bf7\u4e0b\u8f7d\u5b89\u88c5\uff1a\r\nhttps://dotnet.microsoft.com/download/dotnet/10.0", "ScrcpyX \u65e0\u6cd5\u542f\u52a8", 0x10);
                return 1;
            }
            Process.Start(new ProcessStartInfo(app) { WorkingDirectory = dir, UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            try { MessageBox(IntPtr.Zero, "启动预检没有完成。\r\n" + ex.Message, "ScrcpyX 无法启动", 0x10); }
            catch (Exception) { }
            return 3;
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

    private static bool HasDotNet10Desktop()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(root))
                return false;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir).StartsWith("10.", StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception)
        {
        }
        return false;
    }
}
'@ | Set-Content -LiteralPath $launcher -Encoding UTF8
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw 'csc.exe was not found.' }
$outExe = Join-Path $target 'ScrcpyX.exe'
& $csc /nologo /target:winexe /codepage:65001 "/out:$outExe" $launcher
if ($LASTEXITCODE) { throw 'Launcher compile failed.' }
foreach ($name in @(
    'ScrcpyX.runtime.exe',
    'ScrcpyX.runtime.pri',
    'ScrcpyX.pri',
    'ScrcpyX.dll',
    'Microsoft.InteractiveExperiences.Projection.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $name))) {
        throw "Publish is missing $name. Removing it makes WinUI fail fast with 0xC000027B."
    }
}

$zip = Join-Path $publishRoot ("ScrcpyX-v{0}-win-x64.zip" -f $Version)
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $target -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output ("Published: {0}" -f $zip)
Write-Output ("SHA256: {0}" -f $hash)
