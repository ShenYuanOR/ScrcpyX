; ScrcpyX 安装包。只检查 .NET 10，不打包运行时。

#define MyAppName "ScrcpyX"
#define MyAppVersion "0.4.0"
#define MyAppExeName "ScrcpyX.exe"

[Setup]
AppId={{7C4E9A21-6B18-4F0D-9E33-2A8D5C71B6F4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=ScrcpyX
DefaultDirName={localappdata}\Programs\ScrcpyX
DefaultGroupName=ScrcpyX
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=ScrcpyX-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "default"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupWindowTitle=安装 ScrcpyX
FinishedHeadingLabel=安装完成
FinishedLabel=ScrcpyX 已安装。%n%n首次启动前请确保已安装 Windows App Runtime 1.8。%nhttps://learn.microsoft.com/windows/apps/windows-app-sdk/downloads
ClickFinish=完成

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："

[Files]
Source: "..\publish\ScrcpyX\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*"
Source: "..\publish\ScrcpyX\data\*"; DestDir: "{app}\data"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall

[Icons]
Name: "{group}\ScrcpyX"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ScrcpyX"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 ScrcpyX"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function HasDotNet10(): Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(Root) then
    Exit;
  if not FindFirst(Root + '\*', FindRec) then
    Exit;
  try
    repeat
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
         (FindRec.Name <> '.') and (FindRec.Name <> '..') and
         (Pos('10.', FindRec.Name) = 1) then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure OpenDownload(const Url: String);
var
  ErrorCode: Integer;
begin
  try
    ShellExec('', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  except
  end;
end;

function InitializeSetup(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  if HasDotNet10() then
    Exit;
  OpenDownload('https://dotnet.microsoft.com/download/dotnet/10.0');
  Answer := MsgBox('缺少 .NET 10，ScrcpyX 首次启动时无法运行。' #13#10#13#10 +
    '请下载安装：' #13#10 +
    'https://dotnet.microsoft.com/download/dotnet/10.0' #13#10#13#10 +
    '仍要继续安装吗？', mbConfirmation, MB_YESNO);
  Result := Answer = IDYES;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (not WizardSilent) then
    OpenDownload('https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    UninstallProgressForm.StatusLabel.Caption :=
      '程序文件已删除。用户配置仍保留在 data 目录，没有删除。';
end;
