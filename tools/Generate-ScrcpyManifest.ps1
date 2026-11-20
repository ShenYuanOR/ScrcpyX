param(
    [string]$Scrcpy = (Join-Path $PSScriptRoot '..\DLL\scrcpy.exe'),
    [string]$Output = (Join-Path $PSScriptRoot '..\DLL\scrcpy-options.json')
)

$ErrorActionPreference = 'Stop'
$helpLines = @(& $Scrcpy --help 2>&1)
$versionText = (& $Scrcpy --version 2>&1 | Out-String)
$version = [regex]::Match($versionText, 'scrcpy\s+([0-9]+(?:\.[0-9]+)+)').Groups[1].Value
if (-not $version) { throw '无法读取 scrcpy 版本。' }

$official = @{}
$formats = @{}
for ($i = 0; $i -lt $helpLines.Count; $i++) {
    $line = [string]$helpLines[$i]
    if ($line -notmatch '^    (?:-[A-Za-z], )?(--[a-z0-9][a-z0-9-]*)(.*)$') { continue }
    $argument = $Matches[1]
    $formats[$argument] = $Matches[2].Trim().TrimStart('=')
    $description = [System.Collections.Generic.List[string]]::new()
    for ($j = $i + 1; $j -lt $helpLines.Count; $j++) {
        $next = [string]$helpLines[$j]
        if ($next -match '^    -' -or $next -eq 'Shortcuts:') { break }
        if ($next.Trim()) { $description.Add($next.Trim()) }
    }
    $official[$argument] = $description -join ' '
}

# argument|中文标题|group|section|control|allowed values
$metadata = @'
--always-on-top|窗口置顶|window|窗口模式|toggle|
--angle|自定义画面角度|capture|方向与旋转|number|
--audio-bit-rate|音频比特率|audio|编码|text|
--audio-buffer|音频缓冲|audio|缓冲与兼容|integer|
--audio-codec|音频编码格式|audio|编码|select|opus,aac,flac,raw
--audio-codec-options|音频编码器选项|audio|编码|text|
--audio-dup|设备端保留播放|audio|来源与开关|toggle|
--audio-encoder|音频编码器|audio|编码|text|
--audio-output-buffer|SDL 音频输出缓冲|audio|缓冲与兼容|integer|
--audio-source|音频来源|audio|来源与开关|select|output,playback,mic,mic-unprocessed,mic-camcorder,mic-voice-recognition,mic-voice-communication,voice-call,voice-call-uplink,voice-call-downlink,voice-performance
--background-color|虚拟显示背景色|virtual|应用与外观|color|
--camera-ar|相机宽高比|camera|尺寸|text|
--camera-facing|相机朝向|camera|相机选择|select|front,back,external
--camera-fps|相机帧率|camera|采集|number|
--camera-high-speed|高速相机模式|camera|采集|toggle|
--camera-id|相机 ID|camera|相机选择|text|
--camera-size|相机尺寸|camera|尺寸|text|
--camera-torch|启动相机补光灯|camera|采集|toggle|
--camera-zoom|相机初始缩放|camera|采集|number|
--capture-orientation|捕获方向|capture|方向与旋转|select|0,90,180,270,flip0,flip90,flip180,flip270,@,@0,@90,@180,@270
--crop|画面裁剪|capture|捕获区域|text|
--disable-screensaver|运行时禁用屏保|playback|播放与系统|toggle|
--display-id|显示器 ID|capture|捕获来源|integer|
--display-ime-policy|输入法显示策略|virtual|系统行为|select|local,fallback,hide
--display-orientation|显示方向|capture|方向与旋转|select|0,90,180,270,flip0,flip90,flip180,flip270
--flex-display|窗口联动虚拟显示尺寸|virtual|创建与尺寸|toggle|
--force-adb-forward|强制 ADB forward|device|ADB 通道|toggle|
--fullscreen|全屏启动|window|窗口模式|toggle|
--gamepad|手柄输入模式|otg|输入设备|select|disabled,uhid,aoa
--ignore-video-encoder-constraints|忽略视频编码器约束|video|缓冲与兼容|toggle|
--keep-active|保持设备活跃|android|电源与屏幕|toggle|
--keyboard|键盘输入模式|otg|输入设备|select|disabled,sdk,uhid,aoa
--kill-adb-on-close|关闭时结束 ADB|device|ADB 通道|toggle|
--legacy-paste|旧版粘贴方式|input|键盘与剪贴板|toggle|
--max-fps|最大帧率|video|编码与画质|number|
--max-size|最大画面尺寸|video|编码与画质|integer|
--min-size-alignment|最小尺寸对齐|video|缓冲与兼容|select|1,2,4,8,16
--mouse|鼠标输入模式|otg|输入设备|select|disabled,sdk,uhid,aoa
--mouse-bind|鼠标辅助键绑定|input|鼠标输入|text|
--new-display|新建虚拟显示器|virtual|创建与尺寸|text|
--no-audio|禁用音频转发|audio|来源与开关|toggle|
--no-audio-playback|禁用本机音频播放|audio|来源与开关|toggle|
--no-cleanup|退出时不清理设备状态|android|文件与会话|toggle|
--no-clipboard-autosync|禁用剪贴板自动同步|input|键盘与剪贴板|toggle|
--no-control|禁用设备控制|input|控制与快捷键|toggle|
--no-downsize-on-error|编码失败时不自动降分辨率|video|缓冲与兼容|toggle|
--no-key-repeat|禁用按键重复|input|键盘与剪贴板|toggle|
--no-mipmaps|禁用 Mipmap|playback|渲染器与质量|toggle|
--no-mouse-hover|禁用鼠标悬停事件|input|鼠标输入|toggle|
--no-playback|禁用全部本机播放|playback|播放与系统|toggle|
--no-power-on|启动时不唤醒设备|android|电源与屏幕|toggle|
--no-terminal-title|禁用终端标题更新|session|终端行为|toggle|
--no-vd-destroy-content|虚拟显示移除时保留内容|virtual|系统行为|toggle|
--no-vd-system-decorations|虚拟显示不显示系统装饰|virtual|系统行为|toggle|
--no-video|禁用视频转发|video|视频开关|toggle|
--no-video-playback|禁用本机视频播放|playback|播放与系统|toggle|
--no-window|不创建投屏窗口|window|窗口模式|toggle|
--no-window-aspect-ratio-lock|不锁定窗口宽高比|window|尺寸与位置|toggle|
--orientation|统一显示与录制方向|capture|方向与旋转|select|0,90,180,270
--otg|OTG 模式|otg|模式|toggle|
--pause-on-exit|退出时暂停终端|session|终端行为|select|true,false,if-error
--port|客户端监听端口范围|device|ADB 通道|text|
--power-off-on-close|关闭时熄灭设备屏幕|android|电源与屏幕|toggle|
--prefer-text|优先注入文本事件|input|键盘与剪贴板|toggle|
--print-fps|输出 FPS|session|日志与诊断|toggle|
--push-target|拖放文件目标目录|android|文件与会话|path|
--raw-key-events|仅注入原始按键事件|input|键盘与剪贴板|toggle|
--record|录制文件|recording|录制输出|file|
--record-format|录制格式|recording|封装格式|select|mp4,mkv,m4a,mka,opus,aac,flac,wav
--record-orientation|录制方向|recording|画面方向|select|0,90,180,270
--render-driver|SDL 渲染驱动|playback|渲染器与质量|select|direct3d,opengl,opengles2,opengles,metal,software
--render-fit|画面适配模式|playback|渲染器与质量|select|letterbox,stretched,unscaled
--require-audio|音频失败时终止|audio|缓冲与兼容|toggle|
--screen-off-timeout|设备熄屏超时|android|电源与屏幕|number|
--select-tcpip|自动选择 TCP/IP 设备|device|设备选择|toggle|
--select-usb|自动选择 USB 设备|device|设备选择|toggle|
--serial|设备序列号|device|设备选择|text|
--shortcut-mod|快捷键修饰键|input|控制与快捷键|text|
--show-touches|显示物理触摸点|android|交互反馈|toggle|
--start-app|启动 Android 应用|virtual|应用与外观|text|
--stay-awake|保持设备唤醒|android|电源与屏幕|toggle|
--tcpip|配置并连接 TCP/IP|device|网络连接|text|
--time-limit|会话时长上限|session|会话限制|number|
--tunnel-host|ADB 隧道主机|device|ADB 通道|text|
--tunnel-port|ADB 隧道端口|device|ADB 通道|integer|
--turn-screen-off|启动后关闭设备屏幕|android|电源与屏幕|toggle|
--verbosity|日志级别|session|日志与诊断|select|verbose,debug,info,warn,error
--video-bit-rate|视频比特率|video|编码与画质|text|
--video-buffer|视频缓冲|video|缓冲与兼容|integer|
--video-codec|视频编码格式|video|编码与画质|select|h264,h265,av1,vp8,vp9
--video-codec-options|视频编码器选项|video|编码与画质|text|
--video-encoder|视频编码器|video|编码与画质|text|
--video-source|视频来源|capture|捕获来源|select|display,camera
--window-borderless|无边框窗口|window|窗口模式|toggle|
--window-height|初始窗口高度|window|尺寸与位置|integer|
--window-title|窗口标题|window|标题|text|
--window-width|初始窗口宽度|window|尺寸与位置|integer|
--window-x|初始窗口横坐标|window|尺寸与位置|text|
--window-y|初始窗口纵坐标|window|尺寸与位置|text|
'@ -split "`r?`n" | Where-Object { $_ }

$groups = @(
    [ordered]@{ id='device'; title='设备与连接'; order=1 },
    [ordered]@{ id='capture'; title='捕获源与屏幕'; order=2 },
    [ordered]@{ id='camera'; title='相机'; order=3 },
    [ordered]@{ id='virtual'; title='虚拟显示与应用'; order=4 },
    [ordered]@{ id='video'; title='视频'; order=5 },
    [ordered]@{ id='audio'; title='音频'; order=6 },
    [ordered]@{ id='playback'; title='播放与渲染'; order=7 },
    [ordered]@{ id='window'; title='投屏窗口'; order=8 },
    [ordered]@{ id='recording'; title='录制'; order=9 },
    [ordered]@{ id='input'; title='控制与输入'; order=10 },
    [ordered]@{ id='android'; title='Android 设备行为'; order=11 },
    [ordered]@{ id='otg'; title='OTG'; order=12 },
    [ordered]@{ id='session'; title='会话与诊断'; order=13 }
)

$docs = @{
    device='connection.md'; capture='video.md'; camera='camera.md'; virtual='virtual_display.md'
    video='video.md'; audio='audio.md'; playback='video.md'; window='window.md'
    recording='recording.md'; input='control.md'; android='device.md'; otg='otg.md'; session='connection.md'
}

function Get-EnglishTitle([string]$argument) {
    $title = (Get-Culture).TextInfo.ToTitleCase($argument.TrimStart('-').Replace('-', ' '))
    $title.Replace('Adb', 'ADB').Replace('Tcpip', 'TCP/IP').Replace('Usb', 'USB').Replace('Ime', 'IME').Replace('Fps', 'FPS').Replace('Id', 'ID').Replace('Otg', 'OTG').Replace(' Ar', ' AR').Replace('Vd ', 'VD ').Replace('Sdl', 'SDL')
}

function Get-Relations([string]$argument) {
    $enabled = switch -Regex ($argument) {
        '^--camera-' { @('--video-source=camera') }
        '^--audio-(?!source)' { @('!--no-audio') }
        '^--record-(format|orientation)$' { @('--record') }
        '^--window-' { @('!--no-window', '!--no-video-playback') }
        default { @() }
    }
    $conflicts = switch ($argument) {
        '--camera-id' { @('--camera-facing') }
        '--camera-facing' { @('--camera-id') }
        '--camera-size' { @('--camera-ar', '--max-size') }
        '--camera-ar' { @('--camera-size') }
        '--display-id' { @('--new-display') }
        '--new-display' { @('--display-id', '--no-video') }
        '--flex-display' { @('--crop', '--window-width', '--window-height', '--no-control') }
        '--prefer-text' { @('--raw-key-events') }
        '--raw-key-events' { @('--prefer-text') }
        '--select-usb' { @('--serial', '--select-tcpip') }
        '--select-tcpip' { @('--serial', '--select-usb') }
        '--serial' { @('--select-usb', '--select-tcpip') }
        default { @() }
    }
    return @($enabled), @($conflicts)
}

$options = [System.Collections.Generic.List[object]]::new()
$order = 0
foreach ($row in $metadata) {
    $columns = $row.Split('|')
    $argument, $chineseTitle, $group, $section, $control, $values = $columns
    if (-not $official.ContainsKey($argument)) { throw "帮助中缺少参数：$argument" }
    $relations = Get-Relations $argument
    $format = $formats[$argument]
    $optional = $argument -in @('--new-display', '--pause-on-exit', '--tcpip')
    $allowed = @()
    if ($values) { $allowed = @($values.Split(',')) }
    $exampleValue = if ($allowed.Count) { $allowed[0] } elseif ($optional) { '' } else { $format.Trim('[',']','<','>') }
    $example = if ($control -eq 'toggle' -or -not $exampleValue) { "scrcpy $argument" } else { "scrcpy $argument=$exampleValue" }
    $options.Add([ordered]@{
        argument=$argument
        chineseTitle=$chineseTitle
        englishTitle=(Get-EnglishTitle $argument)
        group=$group
        section=$section
        order=(++$order)
        control=$control
        optionalValue=$optional
        valueFormat=$format
        defaultState='scrcpy-default'
        allowedValues=$allowed
        enabledWhen=$relations[0]
        conflicts=$relations[1]
        platform='windows'
        help=$(if ($control -eq 'toggle') { "启用 [$chineseTitle]。关闭时沿用 scrcpy 默认行为。" } else { "设置 [$chineseTitle]。未启用时沿用 scrcpy 默认值。" })
        officialHelp=$official[$argument]
        example=$example
        documentation="https://github.com/Genymobile/scrcpy/blob/v$version/doc/$($docs[$group])"
        risky=($argument -in @('--kill-adb-on-close', '--no-cleanup', '--power-off-on-close'))
    })
}

$expected = @($official.Keys | Where-Object { $_ -notin @('--help','--version','--list-apps','--list-cameras','--list-camera-sizes','--list-displays','--list-encoders','--v4l2-sink','--v4l2-buffer') } | Sort-Object)
$actual = @($options.Argument | Sort-Object)
$missing = @($expected | Where-Object { $_ -notin $actual })
$extra = @($actual | Where-Object { $_ -notin $expected })
if ($options.Count -ne 99 -or $missing.Count -or $extra.Count) {
    throw "参数差集不为零。count=$($options.Count), missing=$($missing -join ','), extra=$($extra -join ',')"
}

$manifest = [ordered]@{
    scrcpyVersion=$version
    generatedFrom="scrcpy $version --help"
    groups=$groups
    options=$options
}
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText((Resolve-Path (Split-Path $Output)).Path + '\' + (Split-Path $Output -Leaf), $json, [System.Text.UTF8Encoding]::new($false))
Write-Output "Generated $($options.Count) Windows options for scrcpy $version -> $Output"
