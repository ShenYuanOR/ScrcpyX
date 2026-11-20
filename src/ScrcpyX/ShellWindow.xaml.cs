using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ScrcpyX;

public sealed partial class ShellWindow : Window
{
    private readonly AppPaths _paths;
    private readonly ScrcpyManifest _manifest;
    private readonly ScrcpyTools _tools;
    private readonly DeviceFiles _files;
    private readonly UserSettings _settings;
    private readonly Dictionary<string, OptionState> _states;
    private readonly SavedWindowState _savedWindow;
    private List<DeviceInfo> _devices = [];
    private MirrorSession? _session;
    private H264Decoder? _decoder;
    private readonly object _decodeGate = new();
    private readonly object _paint = new();
    private byte[] _latest = [];
    private int _latestW;
    private int _latestH;
    private int _viewW;
    private int _viewH;
    private int _frameWidth;
    private int _frameHeight;
    private int _paintQueued;
    private int _expectedW;
    private int _expectedH;
    private int _layoutDepth;
    private int _applyQueued;
    private bool _hasExpected;
    private bool _placed;
    private bool _portrait = true;
    private double? _userScale;
    private int _homeWindowW;
    private int _homeWindowH;
    private double _homeActionsWidth;
    private bool _advancedAnimating;
    private bool _advancedOpen;
    private Storyboard? _advancedBoard;
    private CancellationTokenSource? _titleMarquee;
    private WriteableBitmap? _bitmap;
    private readonly RollingAverage _renderLatency = new(30);
    private long _latestArrival;
    private double? _renderMs;
    private double? _adbMs;
    private CancellationTokenSource? _adbProbe;
    private bool _pressed;
    private bool _closed = true;
    private bool _loading = true;
    private bool _stopping;
    private bool _filePanelOpen;
    private string? _runningName;
    private string _directory = "/sdcard";
    private bool _filesLoaded;
    private bool _clipCut;
    private DeviceEntry? _menuEntry;
    private readonly List<DeviceEntry> _clip = [];
    private readonly Dictionary<string, IReadOnlyList<DeviceEntry>> _listCache = new(StringComparer.Ordinal);
    private int _listEpoch;
    private int _loadGeneration;
    private CancellationTokenSource? _fileOpCancel;
    private bool _fileOpBusy;

    public ShellWindow(AppPaths paths, ScrcpyManifest manifest, string? warning)
    {
        InitializeComponent();
        _paths = paths;
        _manifest = manifest;
        _tools = new(paths);
        _files = new(paths);
        _settings = JsonStore.Load<UserSettings>(paths.Settings, () => new(), out var settingsWarning);
        _savedWindow = JsonStore.Load<SavedWindowState>(paths.WindowState, () => new(), out _);
        _states = new(_settings.Draft.ToDictionary(x => x.Key, x => x.Value.Clone()), StringComparer.OrdinalIgnoreCase);
        BuildAdvanced();
        AppWindow.Changed += OnWindowChanged;
        Root.ActualThemeChanged += (_, _) => ApplyTitleBar();
        ApplyTitleBar();
        _loading = false;
        if (!string.IsNullOrWhiteSpace(warning) || !string.IsNullOrWhiteSpace(settingsWarning))
            ShowWarning(warning ?? settingsWarning);
        Root.Loaded += async (_, _) =>
        {
            ApplyTitleBar();
            ApplyMinimumWindow();
            RestoreWindow();
            HomeView.LayoutUpdated += (_, _) => ApplyClosedAdvancedWidth();
            ApplyClosedAdvancedWidth();
            await RefreshDevicesAsync();
        };
        Closed += (_, _) =>
        {
            _ = StopMirrorAsync();
            SaveSettings();
            SaveWindow();
        };
    }

    private void ApplyTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        var background = ThemeColor("SolidBackgroundFillColorBaseBrush", Windows.UI.Color.FromArgb(255, 32, 32, 32));
        var foreground = ThemeColor("TextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255));
        var dark = Luma(background) < 0.5;
        var hover = Shift(background, dark ? 28 : -20);
        var pressed = Shift(background, dark ? 44 : -36);
        var title = AppWindow.TitleBar;
        title.BackgroundColor = background;
        title.InactiveBackgroundColor = background;
        title.ForegroundColor = foreground;
        title.InactiveForegroundColor = foreground;
        title.ButtonBackgroundColor = background;
        title.ButtonInactiveBackgroundColor = background;
        title.ButtonForegroundColor = foreground;
        title.ButtonHoverBackgroundColor = hover;
        title.ButtonHoverForegroundColor = foreground;
        title.ButtonPressedBackgroundColor = pressed;
        title.ButtonPressedForegroundColor = foreground;
        title.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(160, foreground.R, foreground.G, foreground.B);
        SetTitleBar(TitleDrag);
        if (IsWindows10() && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            try
            {
                presenter.SetBorderAndTitleBar(true, false);
            }
            catch (Exception)
            {
            }
            CaptionButtons.Visibility = Visibility.Visible;
            CaptionTitle.Visibility = Visibility.Visible;
            UpdateCaptionMaxGlyph();
        }
        try
        {
            var caption = title.Height;
            if (caption > 0 && !IsWindows10())
                TitleBarHost.Height = caption / DpiScale();
        }
        catch (Exception)
        {
        }
    }

    private static bool IsWindows10() => Environment.OSVersion.Version.Build < 22000;

    private void UpdateCaptionMaxGlyph()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        CaptionMaxIcon.Glyph = maximized ? "\uE923" : "\uE922";
    }

    private void CaptionMin_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void CaptionMax_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        if (presenter.State == OverlappedPresenterState.Maximized)
            presenter.Restore();
        else
            presenter.Maximize();
        UpdateCaptionMaxGlyph();
    }

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();

    private static Windows.UI.Color ThemeColor(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            return brush.Color;
        return fallback;
    }

    private static double Luma(Windows.UI.Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static Windows.UI.Color Shift(Windows.UI.Color color, int delta)
    {
        static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
        return Windows.UI.Color.FromArgb(color.A == 0 ? (byte)255 : color.A, Clamp(color.R + delta), Clamp(color.G + delta), Clamp(color.B + delta));
    }

    private static readonly string[] AdvancedArguments =
    [
        "--max-size",
        "--max-fps",
        "--video-bit-rate",
        "--stay-awake",
        "--turn-screen-off"
    ];

    private void BuildAdvanced()
    {
        foreach (var argument in AdvancedArguments)
        {
            var option = _manifest.Options.FirstOrDefault(x => x.Argument.Equals(argument, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
                AdvancedRows.Children.Add(OptionRow(option));
        }
    }

    private FrameworkElement OptionRow(OptionDefinition option)
    {
        var state = State(option.Argument);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = option.ChineseTitle, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        FrameworkElement editor;
        if (option.Control == "toggle")
        {
            var toggle = new ToggleSwitch { IsOn = state.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) =>
            {
                if (_loading)
                    return;
                state.Enabled = toggle.IsOn;
                SaveSettings();
            };
            editor = toggle;
        }
        else
        {
            var stored = state.Value.Trim();
            if (stored.Length == 0 || string.Equals(stored, option.ValueFormat, StringComparison.Ordinal))
                stored = "";
            state.Value = stored;
            var box = new TextBox { Text = stored, MinWidth = 180, PlaceholderText = PlaceholderFor(option) };
            box.LostFocus += (_, _) =>
            {
                state.Value = box.Text.Trim();
                state.Enabled = state.Value.Length > 0;
                SaveSettings();
            };
            editor = box;
        }
        Grid.SetColumn(editor, 1);
        grid.Children.Add(label);
        grid.Children.Add(editor);
        return grid;
    }

    private static string PlaceholderFor(OptionDefinition option) => option.Argument switch
    {
        "--video-bit-rate" => "例如 16M 或 8000000",
        "--max-size" => "例如 1920",
        "--max-fps" => "例如 60",
        _ => ""
    };

    private OptionState State(string argument)
    {
        if (!_states.TryGetValue(argument, out var state))
            _states[argument] = state = new();
        return state;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private void ApplyMinimumWindow()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        var dpi = DpiScale();
        presenter.PreferredMinimumWidth = Dip(320, dpi);
        presenter.PreferredMinimumHeight = Dip(180, dpi);
    }

    private void HomeActions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _homeActionsWidth = HomeActions.ActualWidth;
        if (!_advancedOpen && !_advancedAnimating)
            ApplyClosedAdvancedWidth();
    }

    private void ApplyClosedAdvancedWidth()
    {
        if (_advancedOpen || _advancedAnimating)
            return;
        var width = ClosedAdvancedWidth();
        if (width <= 0 || Math.Abs(AdvancedHost.Width - width) < 1)
            return;
        AdvancedHost.MinWidth = 0;
        AdvancedHost.MaxWidth = width;
        AdvancedHost.Width = width;
    }

    private double ClosedAdvancedWidth()
    {
        double width = 0;
        var count = 0;
        foreach (var child in HomeActions.Children)
        {
            if (child is FrameworkElement element && element.ActualWidth > 0)
            {
                width += element.ActualWidth;
                count++;
            }
        }
        if (count > 1)
            width += HomeActions.Spacing * (count - 1);
        if (width > 0)
            return width;
        return _homeActionsWidth > 0 ? _homeActionsWidth : HomeActions.ActualWidth;
    }

    private double ExpandedAdvancedWidth() => Math.Max(ClosedAdvancedWidth() + 180, 460);

    private void HomeView_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutAdvancedScroll();

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedAnimating)
            return;
        if (_advancedOpen)
            BeginCollapse();
        else
            BeginExpand();
    }

    private void BeginExpand()
    {
        _advancedOpen = true;
        _advancedAnimating = true;
        AdvancedChevronRotate.Angle = 0;
        AdvancedToggle.CornerRadius = new CornerRadius(4);
        SetBodyHeight(0);
        var target = ExpandedAdvancedWidth();
        AdvancedHost.MinWidth = 0;
        AdvancedHost.MaxWidth = target;
        var from = AdvancedHost.Width;
        if (double.IsNaN(from) || from <= 0)
            from = AdvancedHost.ActualWidth > 0 ? AdvancedHost.ActualWidth : target;
        Play(AdvancedHost, "Width", from, target, 250, () => AdvancedHost.Width = target, ExpandChevron);
    }

    private void ExpandChevron()
    {
        Play(AdvancedChevronRotate, "Angle", 0, 180, 150, () => AdvancedChevronRotate.Angle = 180, ExpandBody);
    }

    private void ExpandBody()
    {
        AdvancedToggle.CornerRadius = new CornerRadius(4, 4, 0, 0);
        var height = ContentViewportHeight();
        Play(AdvancedBody, "Height", 0, height, 200, () =>
        {
            SetBodyHeight(height);
            _advancedAnimating = false;
            RevealScrollBar(AdvancedScroll);
        }, () => { });
    }

    private void BeginCollapse()
    {
        _advancedAnimating = true;
        var from = AdvancedBody.Height;
        if (double.IsNaN(from) || from < 0)
            from = AdvancedBody.ActualHeight;
        Play(AdvancedBody, "Height", from, 0, 200, () => SetBodyHeight(0), CollapseChevron);
    }

    private void CollapseChevron()
    {
        AdvancedToggle.CornerRadius = new CornerRadius(4);
        Play(AdvancedChevronRotate, "Angle", 180, 0, 150, () => AdvancedChevronRotate.Angle = 0, CollapseWidth);
    }

    private void CollapseWidth()
    {
        var target = ClosedAdvancedWidth();
        var from = AdvancedHost.Width;
        if (double.IsNaN(from) || from <= 0)
            from = AdvancedHost.ActualWidth > 0 ? AdvancedHost.ActualWidth : target;
        AdvancedHost.MaxWidth = Math.Max(from, target);
        Play(AdvancedHost, "Width", from, target, 250, () =>
        {
            _advancedOpen = false;
            _advancedAnimating = false;
            AdvancedHost.Width = target;
            AdvancedHost.MaxWidth = target;
            ApplyClosedAdvancedWidth();
        }, () => { });
    }

    private void Play(DependencyObject target, string property, double from, double to, int milliseconds, Action apply, Action next)
    {
        _advancedBoard?.Stop();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var board = new Storyboard();
        board.Children.Add(animation);
        var finished = false;
        _advancedBoard = board;
        board.Completed += (_, _) =>
        {
            if (finished || !ReferenceEquals(_advancedBoard, board))
                return;
            finished = true;
            _advancedBoard = null;
            board.Stop();
            apply();
            next();
        };
        board.Begin();
    }

    private void AdvancedBody_SizeChanged(object sender, SizeChangedEventArgs e) => ClipAdvancedBody();

    private void SetBodyHeight(double height)
    {
        AdvancedBody.Height = height;
        ClipAdvancedBody();
    }

    private void ClipAdvancedBody()
    {
        AdvancedBody.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, 4000, Math.Max(0, AdvancedBody.ActualHeight))
        };
    }

    private double ContentViewportHeight()
    {
        var slot = Math.Max(32, AdvancedHost.ActualWidth - 16);
        AdvancedContent.Measure(new Windows.Foundation.Size(slot, double.PositiveInfinity));
        var desired = AdvancedContent.DesiredSize.Height + 16;
        var cap = AdvancedCap();
        if (desired < 1)
            desired = cap;
        return Math.Min(desired, cap);
    }

    private double AdvancedCap()
    {
        double available = 140;
        try
        {
            var top = AdvancedBody.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var clientDip = AppWindow.ClientSize.Height / Math.Max(1, DpiScale());
            available = clientDip - top - 12;
        }
        catch (Exception)
        {
            available = 140;
        }
        if (available < 64 || available > 220)
            available = 140;
        return available;
    }

    private void LayoutAdvancedScroll()
    {
        if (!_advancedOpen || _advancedAnimating || Root.ActualHeight <= 0)
            return;
        var height = ContentViewportHeight();
        if (double.IsNaN(AdvancedBody.Height) || Math.Abs(AdvancedBody.Height - height) > 1)
            SetBodyHeight(height);
        AdvancedScroll.UpdateLayout();
        RevealScrollBar(AdvancedScroll);
    }

    private static void RevealScrollBar(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollBar bar && bar.Orientation == Orientation.Vertical)
            {
                bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
                bar.Opacity = 1;
                VisualStateManager.GoToState(bar, "MouseIndicator", false);
            }
            RevealScrollBar(child);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            _devices = await _tools.GetDevicesAsync();
            var ready = _devices.Where(x => x.Ready).ToList();
            var selectedSerial = (DeviceBox.SelectedItem as DeviceInfo)?.Serial;
            _loading = true;
            try
            {
                DeviceBox.ItemsSource = ready;
                DeviceBox.DisplayMemberPath = nameof(DeviceInfo.ShortName);
                var match = selectedSerial is null ? null : ready.FirstOrDefault(x => x.Serial == selectedSerial);
                if (match is not null)
                    DeviceBox.SelectedItem = match;
                else if (ready.Count > 0)
                    DeviceBox.SelectedIndex = 0;
                DeviceBox.Visibility = ready.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _loading = false;
            }
            ApplyDeviceTitle();
        }
        catch (Exception ex)
        {
            if (_session is null)
                StatusText.Text = "找不到手机。";
            ShowWarning("无法运行 adb：" + ex.Message);
            UpdateRunningState();
        }
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            ApplyDeviceTitle();
    }

    private void ApplyDeviceTitle()
    {
        if (_session is not null)
        {
            if (!string.IsNullOrWhiteSpace(_runningName))
                SetDeviceTitle(_runningName);
            StatusText.Text = "正在投屏。停止后才能切换设备或修改参数。";
            UpdateRunningState();
            return;
        }
        var device = SelectedDevice();
        if (device is null)
        {
            SetDeviceTitle(_devices.Any(x => x.Unauthorized) ? "等待手机授权" : "还没有手机");
            StatusText.Text = _devices.Any(x => x.Unauthorized)
                ? "在手机上点允许 USB 调试，然后刷新。"
                : "用数据线连上，并打开 USB 调试。";
            UpdateRunningState();
            return;
        }
        SetDeviceTitle(device.ShortName);
        StatusText.Text = "已连接。默认使用手机原来的分辨率。";
        UpdateRunningState();
    }

    private void SetDeviceTitle(string text)
    {
        if (DeviceTitle.Text != text)
            DeviceTitle.Text = text;
        UpdateTitleMarquee();
    }

    private void UpdateTitleMarquee()
    {
        _titleMarquee?.Cancel();
        _titleMarquee = new CancellationTokenSource();
        var token = _titleMarquee.Token;
        _ = RunTitleMarqueeAsync(token);
    }

    private async Task RunTitleMarqueeAsync(CancellationToken token)
    {
        try
        {
            DeviceTitle.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 80));
            var textWidth = DeviceTitle.DesiredSize.Width;
            var limit = DeviceTitleScroll.MaxWidth;
            if (double.IsNaN(limit) || double.IsInfinity(limit) || limit <= 0)
                limit = 240;
            if (textWidth <= limit + 0.5)
            {
                DeviceTitleScroll.Width = textWidth > 0 ? textWidth : double.NaN;
                DeviceTitleScroll.ChangeView(0, null, null, true);
                return;
            }
            DeviceTitleScroll.Width = limit;
            DeviceTitleScroll.UpdateLayout();
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(4000, token);
                await MoveTitleAsync(token);
                await Task.Delay(1200, token);
                await MoveTitleAsync(token, toStart: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MoveTitleAsync(CancellationToken token, bool toStart = false)
    {
        var extent = DeviceTitleScroll.ScrollableWidth;
        if (extent <= 1)
            return;
        var from = toStart ? extent : 0;
        var to = toStart ? 0 : extent;
        var duration = Math.Max(1400, extent * 12);
        var started = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            var progress = (DateTime.UtcNow - started).TotalMilliseconds / duration;
            if (progress >= 1)
            {
                DeviceTitleScroll.ChangeView(to, null, null, true);
                return;
            }
            var eased = progress * progress * (3 - 2 * progress);
            DeviceTitleScroll.ChangeView(from + (to - from) * eased, null, null, true);
            await Task.Delay(16, token);
        }
    }

    private void UpdateRunningState()
    {
        var running = _session is not null;
        StartButton.Content = running ? "停止投屏" : "开始投屏";
        StartButton.IsEnabled = !_stopping && (running || SelectedDevice() is not null);
        DeviceBox.IsEnabled = !running;
        AdvancedPanel.IsEnabled = !running;
    }

    private DeviceInfo? SelectedDevice() =>
        DeviceBox.SelectedItem as DeviceInfo ?? _devices.FirstOrDefault(x => x.Ready);

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping)
            return;
        if (_session is not null)
        {
            await StopMirrorAsync();
            return;
        }
        var device = SelectedDevice();
        if (device is null)
            return;
        var error = MirrorInput.Validate(InputValue("--max-size"), InputValue("--max-fps"), InputValue("--video-bit-rate"));
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }
        MirrorSession? created = null;
        try
        {
            created = new MirrorSession(_paths);
            OpenDecoder();
        }
        catch (Exception ex)
        {
            CloseDecoder();
            if (created is not null)
            {
                try
                {
                    await created.DisposeAsync();
                }
                catch (Exception)
                {
                }
            }
            ShowWarning(ex.Message);
            return;
        }
        var session = created;
        _runningName = device.ShortName;
        session.Failed += message => DispatcherQueue.TryEnqueue(() =>
        {
            VideoStatus.Text = message;
            VideoStatus.Visibility = Visibility.Visible;
            ShowWarning(message);
        });
        session.FrameSizeChanged += (width, height) => DispatcherQueue.TryEnqueue(() => OnFrameSize(width, height));
        session.FrameReceived += OnFrame;
        _session = session;
        ShowMirrorView();
        ApplyDeviceTitle();
        StartAdbProbe(device.Serial);
        try
        {
            await session.RunAsync(device.Serial, CurrentOptions());
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_session, session))
                await StopMirrorAsync();
        }
    }

    private void ShowMirrorView()
    {
        RememberHomeSize();
        FileView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Collapsed;
        VideoStatus.Text = "正在连接手机…";
        VideoStatus.Visibility = Visibility.Visible;
        ResetLatency();
        MirrorView.Visibility = Visibility.Visible;
        UpdateViewSize();
    }

    private async void StopView_Click(object sender, RoutedEventArgs e) => await StopMirrorAsync();

    private void HideMirrorView()
    {
        _filePanelOpen = false;
        _userScale = null;
        _hasExpected = false;
        _placed = false;
        _expectedW = 0;
        _expectedH = 0;
        PlacePanel();
        FrameImage.Source = null;
        _bitmap = null;
        _pressed = false;
        MirrorView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        ApplyDeviceTitle();
        RestoreHomeSize();
    }

    private void RememberHomeSize()
    {
        try
        {
            var size = AppWindow.Size;
            if (size.Width > 0 && size.Height > 0)
            {
                _homeWindowW = size.Width;
                _homeWindowH = size.Height;
            }
        }
        catch (Exception ex)
        {
            ReportWindowError("RememberHomeSize", ex);
        }
    }

    private void RestoreHomeSize()
    {
        if (_homeWindowW <= 0 || _homeWindowH <= 0)
            return;
        try
        {
            AppWindow.Resize(new SizeInt32(_homeWindowW, _homeWindowH));
        }
        catch (Exception ex)
        {
            ReportWindowError("RestoreHomeSize", ex);
        }
    }

    private void ToggleFilePanel_Click(object sender, RoutedEventArgs e)
    {
        _filePanelOpen = !_filePanelOpen;
        PlacePanel();
        LayoutMirror(_filePanelOpen ? FitAxis.Expand : FitAxis.None);
        if (_filePanelOpen && !_filesLoaded)
            _ = LoadFilesAsync();
    }

    private void MirrorBack_Click(object sender, RoutedEventArgs e) => _session?.Key(MirrorSession.KeyBack);
    private void MirrorHome_Click(object sender, RoutedEventArgs e) => _session?.Key(MirrorSession.KeyHome);
    private void MirrorRecents_Click(object sender, RoutedEventArgs e) => _session?.Key(MirrorSession.KeyRecents);

    private void OpenDecoder()
    {
        lock (_decodeGate)
        {
            _closed = false;
            _decoder?.Dispose();
            _decoder = new H264Decoder(_paths.DllDirectory);
        }
    }

    private void CloseDecoder()
    {
        lock (_decodeGate)
        {
            _closed = true;
            _decoder?.Dispose();
            _decoder = null;
        }
        _frameWidth = 0;
        _frameHeight = 0;
        lock (_paint)
        {
            _latest = [];
            _latestW = 0;
            _latestH = 0;
        }
    }

    private void OnFrameSize(int width, int height)
    {
        var portrait = height >= width;
        var orientationChanged = _placed && portrait != _portrait;
        _frameWidth = width;
        _frameHeight = height;
        _portrait = portrait;
        if (orientationChanged)
            _userScale = null;
        PlacePanel();
        LayoutMirror(!_placed || orientationChanged ? FitAxis.Both : FitAxis.None);
        _placed = true;
    }

    private void PhoneHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewSize();

    private void UpdateViewSize()
    {
        var dpi = DpiScale();
        var hostW = (int)Math.Round(PhoneHost.ActualWidth * dpi);
        var hostH = (int)Math.Round(PhoneHost.ActualHeight * dpi);
        var viewW = hostW;
        var viewH = hostH;
        if (_frameWidth > 0 && _frameHeight > 0 && hostW >= 16 && hostH >= 16)
        {
            var scale = Math.Min(hostW / (double)_frameWidth, hostH / (double)_frameHeight);
            viewW = Math.Max(16, (int)Math.Round(_frameWidth * scale));
            viewH = Math.Max(16, (int)Math.Round(_frameHeight * scale));
        }
        lock (_decodeGate)
        {
            _viewW = viewW;
            _viewH = viewH;
        }
    }

    private void OnFrame(VideoFrame frame)
    {
        var arrived = Stopwatch.GetTimestamp();
        try
        {
            byte[]? copy = null;
            int width = 0;
            int height = 0;
            lock (_decodeGate)
            {
                if (_closed || _decoder is null)
                    return;
                var present = Volatile.Read(ref _paintQueued) == 0;
                if (!_decoder.Decode(frame.Data, present, _viewW, _viewH, out var pixels, out width, out height))
                    return;
                copy = new byte[pixels.Length];
                Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
            }
            if (copy is null)
                return;
            lock (_paint)
            {
                _latest = copy;
                _latestW = width;
                _latestH = height;
                _latestArrival = arrived;
            }
            if (Interlocked.Exchange(ref _paintQueued, 1) == 1)
                return;
            DispatcherQueue.TryEnqueue(Paint);
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VideoStatus.Text = ex.Message;
                VideoStatus.Visibility = Visibility.Visible;
            });
        }
    }

    private void Paint()
    {
        byte[] pixels;
        int width;
        int height;
        long arrival;
        lock (_paint)
        {
            pixels = _latest;
            width = _latestW;
            height = _latestH;
            arrival = _latestArrival;
        }
        if (width <= 0 || pixels.Length < width * height * 4)
        {
            Interlocked.Exchange(ref _paintQueued, 0);
            return;
        }
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            FrameImage.Source = _bitmap;
        }
        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, width * height * 4);
        }
        _bitmap.Invalidate();
        VideoStatus.Visibility = Visibility.Collapsed;
        if (arrival > 0)
        {
            _renderMs = _renderLatency.Add(Stopwatch.GetElapsedTime(arrival).TotalMilliseconds);
            UpdateLatencyText();
        }
        Interlocked.Exchange(ref _paintQueued, 0);
    }

    private void ResetLatency()
    {
        _renderLatency.Reset();
        _renderMs = null;
        _adbMs = null;
        _latestArrival = 0;
        UpdateLatencyText();
    }

    private void UpdateLatencyText()
    {
        var render = _renderMs is double value ? value.ToString("0", CultureInfo.InvariantCulture) + " ms" : "—";
        var adb = _adbMs is double trip ? trip.ToString("0", CultureInfo.InvariantCulture) + " ms" : "—";
        RenderLatencyText.Text = "渲染 " + render;
        AdbLatencyText.Text = "ADB 往返 " + adb;
    }

    private void StartAdbProbe(string serial)
    {
        StopAdbProbe();
        var cancel = new CancellationTokenSource();
        _adbProbe = cancel;
        _ = ProbeAdbAsync(serial, cancel.Token);
    }

    private void StopAdbProbe()
    {
        _adbProbe?.Cancel();
        _adbProbe?.Dispose();
        _adbProbe = null;
    }

    private async Task ProbeAdbAsync(string serial, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            double? measured = null;
            try
            {
                measured = await _files.EchoMillisecondsAsync(serial, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                measured = null;
            }
            if (cancellationToken.IsCancellationRequested)
                break;
            var shown = measured;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                _adbMs = shown;
                UpdateLatencyText();
            });
            var wait = TimeSpan.FromSeconds(2) - Stopwatch.GetElapsedTime(started);
            if (wait < TimeSpan.FromMilliseconds(200))
                wait = TimeSpan.FromMilliseconds(200);
            try
            {
                await Task.Delay(wait, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Phone_Pressed(object sender, PointerRoutedEventArgs e)
    {
        PhoneHost.CapturePointer(e.Pointer);
        _pressed = true;
        Touch(0, e);
    }

    private void Phone_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed)
            Touch(2, e);
    }

    private void Phone_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
            return;
        _pressed = false;
        Touch(1, e);
        PhoneHost.ReleasePointerCapture(e.Pointer);
    }

    private void Touch(int action, PointerRoutedEventArgs e)
    {
        if (_session is null || _frameWidth <= 0 || _frameHeight <= 0 || PhoneHost.ActualWidth <= 0 || PhoneHost.ActualHeight <= 0)
            return;
        var scale = Math.Min(PhoneHost.ActualWidth / _frameWidth, PhoneHost.ActualHeight / _frameHeight);
        var drawW = _frameWidth * scale;
        var drawH = _frameHeight * scale;
        var offsetX = (PhoneHost.ActualWidth - drawW) / 2;
        var offsetY = (PhoneHost.ActualHeight - drawH) / 2;
        var point = e.GetCurrentPoint(PhoneHost).Position;
        if (point.X < offsetX || point.Y < offsetY || point.X >= offsetX + drawW || point.Y >= offsetY + drawH)
            return;
        var x = (int)Math.Clamp((point.X - offsetX) / drawW * _frameWidth, 0, _frameWidth - 1);
        var y = (int)Math.Clamp((point.Y - offsetY) / drawH * _frameHeight, 0, _frameHeight - 1);
        _session.Touch(action, x, y);
    }

    private enum FitAxis { None, Expand, Both }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
            DispatcherQueue.TryEnqueue(LayoutAdvancedScroll);
        if (args.DidPresenterChange && IsWindows10())
            DispatcherQueue.TryEnqueue(UpdateCaptionMaxGlyph);
        if (_layoutDepth > 0 || _session is null || !args.DidSizeChange || !_hasExpected)
            return;
        if (Interlocked.Exchange(ref _applyQueued, 1) == 1)
            return;
        DispatcherQueue.TryEnqueue(ApplyUserResize);
    }

    private void ApplyUserResize()
    {
        Interlocked.Exchange(ref _applyQueued, 0);
        if (_session is null || _frameWidth <= 0 || _frameHeight <= 0)
            return;
        try
        {
            var client = AppWindow.ClientSize;
            if (client.Width == _expectedW && client.Height == _expectedH)
                return;
            var dpi = DpiScale();
            var bar = Dip(56, dpi);
            var panelW = _filePanelOpen && _portrait ? Dip(360, dpi) : 0;
            var panelH = _filePanelOpen && !_portrait ? Dip(320, dpi) : 0;
            var phoneW = client.Width - bar - panelW;
            var phoneH = client.Height - panelH;
            if (phoneW < 16 || phoneH < 16)
                return;
            var oldW = Math.Max(1, _expectedW - bar - panelW);
            var oldH = Math.Max(1, _expectedH - panelH);
            var dw = Math.Abs(phoneW - oldW);
            var dh = Math.Abs(phoneH - oldH);
            var scale = dw >= dh ? phoneW / (double)_frameWidth : phoneH / (double)_frameHeight;
            if (scale < 0.12)
                scale = 0.12;
            _userScale = scale;
            LayoutMirror(FitAxis.None);
        }
        catch (Exception ex)
        {
            ReportWindowError("AppWindow.Changed", ex);
        }
    }

    private void PlacePanel()
    {
        if (!_filePanelOpen)
        {
            SidePanelColumn.Width = new GridLength(0);
            BottomPanelRow.Height = new GridLength(0);
            FilePanel.Visibility = Visibility.Collapsed;
            ApplyFileTools();
            return;
        }
        FilePanel.Visibility = Visibility.Visible;
        if (_portrait)
        {
            BottomPanelRow.Height = new GridLength(0);
            SidePanelColumn.Width = new GridLength(360);
            Grid.SetRow(FilePanel, 0);
            Grid.SetColumn(FilePanel, 2);
            Grid.SetColumnSpan(FilePanel, 1);
            FilePanel.Width = 360;
            FilePanel.Height = double.NaN;
            FilePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            FilePanel.VerticalAlignment = VerticalAlignment.Stretch;
            ApplyFileTools();
            return;
        }
        SidePanelColumn.Width = new GridLength(0);
        BottomPanelRow.Height = new GridLength(320);
        Grid.SetRow(FilePanel, 1);
        Grid.SetColumn(FilePanel, 0);
        Grid.SetColumnSpan(FilePanel, 3);
        FilePanel.Width = double.NaN;
        FilePanel.Height = 320;
        FilePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        FilePanel.VerticalAlignment = VerticalAlignment.Stretch;
        ApplyFileTools();
    }

    private void ApplyFileTools()
    {
        var landscape = !_portrait;
        PortraitTools.Visibility = landscape ? Visibility.Collapsed : Visibility.Visible;
        LandscapeTools.Visibility = landscape ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LayoutMirror(FitAxis axis)
    {
        if (_frameWidth <= 0 || _frameHeight <= 0 || MirrorView.Visibility != Visibility.Visible)
            return;
        try
        {
            LayoutMirrorCore(axis);
        }
        catch (Exception ex)
        {
            ReportWindowError("Layout", ex);
        }
    }

    private void LayoutMirrorCore(FitAxis axis)
    {
        var dpi = DpiScale();
        RectInt32 work;
        try
        {
            work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        }
        catch (Exception ex)
        {
            ReportWindowError("DisplayArea.GetFromWindowId", ex);
            return;
        }
        var bar = Dip(56, dpi);
        var panelW = _filePanelOpen && _portrait ? Dip(360, dpi) : 0;
        var panelH = _filePanelOpen && !_portrait ? Dip(320, dpi) : 0;
        var decoW = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        var decoH = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        var margin = Dip(8, dpi);
        var availW = Math.Max(1, work.Width - decoW - margin - bar - panelW);
        var availH = Math.Max(1, work.Height - decoH - margin - panelH);
        var fit = Math.Min(availW / (double)_frameWidth, availH / (double)_frameHeight);
        var scale = _userScale ?? Math.Min(1, fit);
        if (scale > fit)
            scale = fit;
        if (scale > 1)
            scale = 1;
        if (scale < 0.12)
            scale = 0.12;
        var phoneW = Math.Max(1, (int)Math.Round(_frameWidth * scale));
        var phoneH = Math.Max(1, (int)Math.Round(_frameHeight * scale));
        var clientW = bar + phoneW + panelW;
        var clientH = phoneH + panelH;
        if (axis != FitAxis.None)
        {
            var pos = AppWindow.Position;
            var outerW = clientW + decoW;
            var outerH = clientH + decoH;
            var overflowW = axis == FitAxis.Both || (_portrait && axis == FitAxis.Expand) ? outerW - work.Width : 0;
            var overflowH = axis == FitAxis.Both || (!_portrait && axis == FitAxis.Expand) ? outerH - work.Height : 0;
            if (overflowW > 0 || overflowH > 0)
            {
                var nextW = overflowW > 0 ? Math.Max(1, phoneW - overflowW) : phoneW;
                var nextH = overflowH > 0 ? Math.Max(1, phoneH - overflowH) : phoneH;
                scale = Math.Min(nextW / (double)_frameWidth, nextH / (double)_frameHeight);
                if (scale < 0.12)
                    scale = 0.12;
                _userScale = scale;
                phoneW = Math.Max(1, (int)Math.Round(_frameWidth * scale));
                phoneH = Math.Max(1, (int)Math.Round(_frameHeight * scale));
                clientW = bar + phoneW + panelW;
                clientH = phoneH + panelH;
                outerW = clientW + decoW;
                outerH = clientH + decoH;
            }
            var x = pos.X;
            var y = pos.Y;
            if (axis == FitAxis.Both || (_portrait && axis == FitAxis.Expand))
            {
                if (x + outerW > work.X + work.Width)
                    x = work.X + work.Width - outerW;
                if (x < work.X)
                    x = work.X;
            }
            if (axis == FitAxis.Both || (!_portrait && axis == FitAxis.Expand))
            {
                if (y + outerH > work.Y + work.Height)
                    y = work.Y + work.Height - outerH;
                if (y < work.Y)
                    y = work.Y;
            }
            if (!TryResizeClient(clientW, clientH))
                return;
            if (x != pos.X || y != pos.Y)
                TryMove(x, y);
        }
        else if (!TryResizeClient(clientW, clientH))
        {
            return;
        }
        var actual = AppWindow.ClientSize;
        _expectedW = Math.Max(1, actual.Width);
        _expectedH = Math.Max(1, actual.Height);
        _hasExpected = true;
        lock (_decodeGate)
        {
            _viewW = phoneW;
            _viewH = phoneH;
        }
    }

    private static int Dip(double dip, double dpi) => Math.Max(1, (int)Math.Round(dip * dpi));

    private bool TryResizeClient(int width, int height)
    {
        try
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var decoW = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
            var decoH = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            var maxW = Math.Max(1, work.Width - decoW);
            var maxH = Math.Max(1, work.Height - decoH);
            width = Math.Clamp(width, 1, maxW);
            height = Math.Clamp(height, 1, maxH);
            _layoutDepth++;
            try
            {
                AppWindow.ResizeClient(new SizeInt32(width, height));
            }
            finally
            {
                _layoutDepth--;
            }
            return true;
        }
        catch (Exception ex)
        {
            ReportWindowError("AppWindow.ResizeClient", ex);
            return false;
        }
    }

    private bool TryMove(int x, int y)
    {
        try
        {
            var size = AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return false;
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var maxX = Math.Max(work.X, work.X + work.Width - size.Width);
            var maxY = Math.Max(work.Y, work.Y + work.Height - size.Height);
            x = Math.Clamp(x, work.X, maxX);
            y = Math.Clamp(y, work.Y, maxY);
            _layoutDepth++;
            try
            {
                AppWindow.Move(new PointInt32(x, y));
            }
            finally
            {
                _layoutDepth--;
            }
            return true;
        }
        catch (Exception ex)
        {
            ReportWindowError("AppWindow.Move", ex);
            return false;
        }
    }

    private void ReportWindowError(string operation, Exception ex)
    {
        var brief = operation + "：" + ex.Message;
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            File.AppendAllText(
                Path.Combine(_paths.LogsDirectory, "mirror-window.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {operation}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
        VideoStatus.Text = brief;
        VideoStatus.Visibility = Visibility.Visible;
        ShowWarning(brief);
    }

    private double DpiScale()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private string? InputValue(string argument)
    {
        var state = State(argument);
        return state.Enabled ? state.Value : null;
    }

    private MirrorOptions CurrentOptions()
    {
        int? ReadNumber(string argument)
        {
            var state = State(argument);
            if (!state.Enabled || !int.TryParse(state.Value, out var value) || value <= 0)
                return null;
            return value;
        }
        var rate = State("--video-bit-rate");
        return new MirrorOptions(
            ReadNumber("--max-size"),
            ReadNumber("--max-fps"),
            rate.Enabled && rate.Value.Trim().Length > 0 ? rate.Value.Trim() : null,
            State("--stay-awake").Enabled,
            State("--turn-screen-off").Enabled);
    }

    private async Task StopMirrorAsync()
    {
        StopAdbProbe();
        ResetLatency();
        if (_session is null)
            return;
        _stopping = true;
        var session = _session;
        _session = null;
        session.FrameReceived -= OnFrame;
        CloseDecoder();
        UpdateRunningState();
        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            _stopping = false;
            _runningName = null;
            HideMirrorView();
        }
    }

    private void ShowFiles_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice() is null)
        {
            ShowWarning("先连上手机，才能看里面的文件。");
            return;
        }
        HomeView.Visibility = Visibility.Collapsed;
        FileView.Visibility = Visibility.Visible;
        _directory = "/sdcard";
        _ = LoadFilesAsync();
    }

    private void ShowHome_Click(object sender, RoutedEventArgs e)
    {
        FileView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
    }

    private static string CacheKey(string path)
    {
        var trimmed = path.Trim().TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private void InvalidateList(string path)
    {
        _listCache.Remove(CacheKey(path));
        _listEpoch++;
    }

    private void ShowEntries(IReadOnlyList<DeviceEntry> entries)
    {
        FileList.ItemsSource = entries;
        PanelFileList.ItemsSource = entries;
        ApplyFileStatus(entries);
    }

    private void ApplyFileStatus(IReadOnlyList<DeviceEntry> entries)
    {
        SetFileStatus(entries.Count + " 项");
        ShowFileMessage(entries.Count == 0 ? "这个文件夹是空的" : null);
        _filesLoaded = true;
        ShowClipStatus();
    }

    private static bool SameListing(IReadOnlyList<DeviceEntry> left, IReadOnlyList<DeviceEntry> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || a.IsDirectory != b.IsDirectory
                || a.Size != b.Size
                || a.UnixSeconds != b.UnixSeconds)
                return false;
        }
        return true;
    }

    private void ShowClipStatus()
    {
        if (_clip.Count == 0)
            return;
        var action = _clipCut ? "已剪切 " : "已复制 ";
        SetFileStatus(action + _clip.Count + " 项，可粘贴到当前目录");
    }

    private async Task LoadFilesAsync()
    {
        var device = SelectedDevice();
        if (device is null)
            return;
        var requestDir = _directory;
        var key = CacheKey(requestDir);
        PathBox.Text = requestDir;
        PanelPathBox.Text = requestDir;
        PanelPathBoxWide.Text = requestDir;
        var generation = ++_loadGeneration;
        var epoch = _listEpoch;
        IReadOnlyList<DeviceEntry>? shown = null;
        if (_listCache.TryGetValue(key, out var cached))
        {
            shown = cached;
            ShowEntries(cached);
        }
        else
        {
            SetFileStatus("正在读取…");
            ShowFileMessage(null);
        }
        try
        {
            var entries = await _files.ListAsync(device.Serial, requestDir);
            if (generation != _loadGeneration || epoch != _listEpoch)
                return;
            _listCache[key] = entries;
            if (CacheKey(_directory) != key)
                return;
            if (shown is not null && SameListing(shown, entries))
            {
                ApplyFileStatus(entries);
                return;
            }
            ShowEntries(entries);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || CacheKey(_directory) != key)
                return;
            SetFileStatus(ex.Message);
            ShowFileMessage(null);
            ShowWarning(ex.Message);
        }
    }

    private void ShowFileMessage(string? text)
    {
        var show = !string.IsNullOrEmpty(text);
        FilePlaceholder.Text = text ?? "";
        PanelFilePlaceholder.Text = text ?? "";
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FilePlaceholder.Visibility = visibility;
        PanelFilePlaceholder.Visibility = visibility;
    }

    private void FileUp_Click(object sender, RoutedEventArgs e)
    {
        var trimmed = _directory.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0)
            return;
        _directory = trimmed[..slash];
        _ = LoadFilesAsync();
    }

    private void FileRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadFilesAsync();

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        if (sender is TextBox box)
            _directory = box.Text.Trim();
        _ = LoadFilesAsync();
    }

    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is ListView list && list.SelectedItem is DeviceEntry { IsDirectory: true } entry)
        {
            _directory = entry.FullPath;
            _ = LoadFilesAsync();
        }
    }

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = KeyIsDown(Windows.System.VirtualKey.Control);
        var shift = KeyIsDown(Windows.System.VirtualKey.Shift);
        if (ctrl && e.Key == Windows.System.VirtualKey.A && sender is ListView list)
        {
            list.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Windows.System.VirtualKey.N)
        {
            _ = MkdirAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Windows.System.VirtualKey.C)
        {
            _ = RememberClipAsync(cut: false);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Windows.System.VirtualKey.X)
        {
            _ = RememberClipAsync(cut: true);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Windows.System.VirtualKey.V)
        {
            _ = PasteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.F2)
        {
            _ = RenameAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.F5)
        {
            _ = LoadFilesAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            _ = DeleteAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Back)
        {
            FileUp_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OpenEntry(ActiveFileList.SelectedItem as DeviceEntry);
            e.Handled = true;
        }
    }

    private static bool KeyIsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _menuEntry = FindEntry(e.OriginalSource);
        if (sender is ListView list && _menuEntry is not null && !list.SelectedItems.Contains(_menuEntry))
            list.SelectedItem = _menuEntry;
    }

    private static DeviceEntry? FindEntry(object source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: DeviceEntry entry })
                return entry;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void DeviceFileMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu)
            return;
        if (_menuEntry is null && SelectedEntries().Count == 1)
            _menuEntry = SelectedEntries()[0];
        var selected = SelectedEntries().Count;
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            item.IsEnabled = item.Text switch
            {
                "打开" => _menuEntry is { IsDirectory: true },
                "复制" or "剪切" or "导出到电脑" or "删除" => selected > 0,
                "粘贴到当前文件夹" => _clip.Count > 0,
                "导入到此文件夹" => true,
                "重命名" => selected == 1,
                _ => true
            };
        }
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenEntry(_menuEntry);

    private void OpenEntry(DeviceEntry? entry)
    {
        if (entry is not { IsDirectory: true })
            return;
        _directory = entry.FullPath;
        _ = LoadFilesAsync();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await RememberClipAsync(cut: false);
    private async void Cut_Click(object sender, RoutedEventArgs e) => await RememberClipAsync(cut: true);
    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync(null);
    private async void PasteInto_Click(object sender, RoutedEventArgs e) => await PasteAsync(_directory);

    private async void ImportInto_Click(object sender, RoutedEventArgs e)
    {
        await PushAsync(_menuEntry is { IsDirectory: true } ? _menuEntry.FullPath : _directory);
    }

    private async Task RememberClipAsync(bool cut)
    {
        var entries = SelectedEntries();
        if (entries.Count == 0 || !BeginFileOp())
            return;
        var done = 0;
        try
        {
            var token = FileOpToken;
            var saved = new List<DeviceEntry>(entries.Count);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ShowFileProgress(entry.Name, done * 100 / entries.Count, done, entries.Count);
                saved.Add(entry);
                done++;
                ShowFileProgress(entry.Name, done * 100 / entries.Count, done, entries.Count);
                await Task.Yield();
            }
            _clip.Clear();
            _clip.AddRange(saved);
            _clipCut = cut;
            FinishFileOp((cut ? "已剪切 " : "已复制 ") + saved.Count + " 项，可粘贴到当前目录");
        }
        catch (OperationCanceledException)
        {
            FinishFileOp(done > 0 ? "已取消，完成 " + done + "/" + entries.Count + " 项。" : "已取消。");
        }
        catch (Exception ex)
        {
            FinishFileOp(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private async Task PasteAsync(string? directory)
    {
        var device = SelectedDevice();
        if (device is null || _clip.Count == 0 || !BeginFileOp())
            return;
        var destDir = string.IsNullOrWhiteSpace(directory) ? _directory : directory;
        var pending = _clip.ToList();
        var done = 0;
        var cancelled = false;
        try
        {
            var token = FileOpToken;
            InvalidateList(destDir);
            if (_clipCut)
            {
                foreach (var item in pending)
                    InvalidateList(item.FullPath[..Math.Max(0, item.FullPath.LastIndexOf('/'))]);
            }
            foreach (var item in pending)
            {
                token.ThrowIfCancellationRequested();
                ShowFileProgress(item.Name, null, done, pending.Count);
                var dest = destDir.TrimEnd('/') + "/" + item.Name;
                if (string.Equals(item.FullPath.TrimEnd('/'), dest.TrimEnd('/'), StringComparison.Ordinal))
                    continue;
                if (item.IsDirectory && dest.TrimEnd('/').StartsWith(item.FullPath.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    ShowWarning("不能把文件夹粘贴到它自己里面。");
                    continue;
                }
                if (await _files.ExistsAsync(device.Serial, dest, token))
                {
                    var choice = await AskPasteAsync(item.Name);
                    if (choice == PasteChoice.Cancel)
                    {
                        cancelled = true;
                        break;
                    }
                    if (choice == PasteChoice.Skip)
                        continue;
                    ShowFileProgress(item.Name, null, done, pending.Count);
                    await _files.DeleteAsync(device.Serial, dest, token);
                }
                if (_clipCut)
                    await _files.MoveAsync(device.Serial, item.FullPath, dest, token);
                else
                    await _files.CopyAsync(device.Serial, item.FullPath, dest, token);
                done++;
                if (_clipCut)
                    _clip.RemoveAll(entry => entry.FullPath == item.FullPath);
            }
            HideFileProgress();
            await LoadFilesAsync();
            FinishFileOp(cancelled
                ? done > 0 ? "已取消，完成 " + done + "/" + pending.Count + " 项。" : "已取消。"
                : (_clipCut ? "已移动 " : "已粘贴 ") + done + " 项。");
        }
        catch (OperationCanceledException)
        {
            try
            {
                await LoadFilesAsync();
            }
            catch (Exception)
            {
            }
            FinishFileOp(done > 0 ? "已取消，完成 " + done + "/" + pending.Count + " 项。" : "已取消。");
        }
        catch (Exception ex)
        {
            FinishFileOp(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private async void Pull_Click(object sender, RoutedEventArgs e) => await PullAsync();
    private async void Push_Click(object sender, RoutedEventArgs e) => await PushAsync();
    private async void Mkdir_Click(object sender, RoutedEventArgs e) => await MkdirAsync();
    private async void Rename_Click(object sender, RoutedEventArgs e) => await RenameAsync();
    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteAsync();

    private ListView ActiveFileList =>
        FilePanel.Visibility == Visibility.Visible ? PanelFileList : FileList;

    private IReadOnlyList<DeviceEntry> SelectedEntries() =>
        ActiveFileList.SelectedItems.OfType<DeviceEntry>().ToList();

    private void SetFileStatus(string text)
    {
        FileStatus.Text = text;
        PanelFileStatus.Text = text;
    }

    private bool BeginFileOp()
    {
        if (_fileOpBusy)
        {
            SetFileStatus("有文件操作正在进行。");
            return false;
        }
        _fileOpBusy = true;
        _fileOpCancel = new CancellationTokenSource();
        return true;
    }

    private CancellationToken FileOpToken => _fileOpCancel?.Token ?? CancellationToken.None;

    private void CancelFileOp_Click(object sender, RoutedEventArgs e) => _fileOpCancel?.Cancel();

    private void ShowFileProgress(string name, int? percent, int completed, int total)
    {
        var label = total > 1 ? name + "  已完成 " + completed + "/" + total : name;
        Apply(FileProgress, FileProgressName, FileProgressBar);
        Apply(PanelFileProgress, PanelFileProgressName, PanelFileProgressBar);
        void Apply(StackPanel host, TextBlock text, ProgressBar bar)
        {
            host.Visibility = Visibility.Visible;
            text.Text = label;
            if (percent is int value)
            {
                bar.IsIndeterminate = false;
                bar.Value = Math.Clamp(value, 0, 100);
            }
            else
            {
                bar.IsIndeterminate = true;
            }
        }
    }

    private void HideFileProgress()
    {
        FileProgress.Visibility = Visibility.Collapsed;
        PanelFileProgress.Visibility = Visibility.Collapsed;
        FileProgressBar.IsIndeterminate = false;
        PanelFileProgressBar.IsIndeterminate = false;
    }

    private void FinishFileOp(string status)
    {
        HideFileProgress();
        SetFileStatus(status);
        _fileOpBusy = false;
        _fileOpCancel?.Dispose();
        _fileOpCancel = null;
    }

    private async Task PullAsync()
    {
        var device = SelectedDevice();
        var entries = SelectedEntries().ToList();
        if (device is null || entries.Count == 0)
            return;
        string? folderPath;
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            folderPath = folder?.Path;
            if (folderPath is null || !BeginFileOp())
                return;
        }
        catch (Exception ex)
        {
            SetFileStatus(ex.Message);
            ShowWarning(ex.Message);
            return;
        }
        var done = 0;
        try
        {
            var token = FileOpToken;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ShowFileProgress(entry.Name, null, done, entries.Count);
                IProgress<int>? progress = entry.IsDirectory
                    ? null
                    : new Progress<int>(value => ShowFileProgress(entry.Name, value, done, entries.Count));
                await _files.PullAsync(device.Serial, entry.FullPath, Path.Combine(folderPath, entry.Name), progress, token);
                done++;
            }
            FinishFileOp("已导出 " + done + " 项。");
        }
        catch (OperationCanceledException)
        {
            FinishFileOp(done > 0 ? "已取消，完成 " + done + "/" + entries.Count + " 项。" : "已取消。");
        }
        catch (Exception ex)
        {
            FinishFileOp(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private Task PushAsync() => PushAsync(_directory);

    private async Task PushAsync(string directory)
    {
        var device = SelectedDevice();
        if (device is null)
            return;
        IReadOnlyList<Windows.Storage.StorageFile> picked;
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0 || !BeginFileOp())
                return;
            picked = files.ToList();
        }
        catch (Exception ex)
        {
            SetFileStatus(ex.Message);
            ShowWarning(ex.Message);
            return;
        }
        var done = 0;
        try
        {
            var token = FileOpToken;
            InvalidateList(directory);
            foreach (var file in picked)
            {
                token.ThrowIfCancellationRequested();
                ShowFileProgress(file.Name, null, done, picked.Count);
                var progress = new Progress<int>(value => ShowFileProgress(file.Name, value, done, picked.Count));
                await _files.PushAsync(device.Serial, file.Path, directory.TrimEnd('/') + "/" + file.Name, progress, token);
                done++;
            }
            HideFileProgress();
            await LoadFilesAsync();
            FinishFileOp("已导入 " + done + " 项。");
        }
        catch (OperationCanceledException)
        {
            try
            {
                await LoadFilesAsync();
            }
            catch (Exception)
            {
            }
            FinishFileOp(done > 0 ? "已取消，完成 " + done + "/" + picked.Count + " 项。" : "已取消。");
        }
        catch (Exception ex)
        {
            FinishFileOp(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private async Task MkdirAsync()
    {
        var device = SelectedDevice();
        if (device is null)
            return;
        try
        {
            var name = await AskAsync("新建文件夹", "");
            if (string.IsNullOrWhiteSpace(name))
                return;
            await _files.MkdirAsync(device.Serial, _directory.TrimEnd('/') + "/" + name.Trim());
            InvalidateList(_directory);
            await LoadFilesAsync();
        }
        catch (Exception ex)
        {
            SetFileStatus(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private async Task RenameAsync()
    {
        var device = SelectedDevice();
        if (device is null || ActiveFileList.SelectedItem is not DeviceEntry entry)
            return;
        try
        {
            var name = await AskAsync("重命名", entry.Name);
            if (string.IsNullOrWhiteSpace(name) || name == entry.Name)
                return;
            var parent = entry.FullPath[..^entry.Name.Length].TrimEnd('/');
            await _files.RenameAsync(device.Serial, entry.FullPath, parent + "/" + name.Trim());
            InvalidateList(parent);
            if (entry.IsDirectory)
                InvalidateList(entry.FullPath);
            await LoadFilesAsync();
        }
        catch (Exception ex)
        {
            SetFileStatus(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        var device = SelectedDevice();
        var entries = SelectedEntries().ToList();
        if (device is null || entries.Count == 0)
            return;
        var names = string.Join("、", entries.Take(3).Select(x => x.Name));
        var message = entries.Count <= 3
            ? "删除 " + names + "？"
            : "删除 " + names + " 等 " + entries.Count + " 项？";
        bool confirmed;
        try
        {
            confirmed = await ConfirmAsync("删除", message);
        }
        catch (Exception ex)
        {
            SetFileStatus(ex.Message);
            ShowWarning(ex.Message);
            return;
        }
        if (!confirmed || !BeginFileOp())
            return;
        var done = 0;
        try
        {
            var token = FileOpToken;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ShowFileProgress(entry.Name, null, done, entries.Count);
                await _files.DeleteAsync(device.Serial, entry.FullPath, token);
                done++;
                if (entry.IsDirectory)
                    InvalidateList(entry.FullPath);
            }
            InvalidateList(_directory);
            HideFileProgress();
            await LoadFilesAsync();
            FinishFileOp("已删除 " + done + " 项。");
        }
        catch (OperationCanceledException)
        {
            InvalidateList(_directory);
            try
            {
                await LoadFilesAsync();
            }
            catch (Exception)
            {
            }
            FinishFileOp(done > 0 ? "已取消，完成 " + done + "/" + entries.Count + " 项。" : "已取消。");
        }
        catch (Exception ex)
        {
            FinishFileOp(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        var device = SelectedDevice();
        if (device is null)
            return;
        IReadOnlyList<Windows.Storage.StorageFile> files;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            files = items.OfType<Windows.Storage.StorageFile>().ToList();
            if (files.Count == 0 || !BeginFileOp())
                return;
        }
        catch (Exception ex)
        {
            SetFileStatus(ex.Message);
            ShowWarning(ex.Message);
            return;
        }
        var done = 0;
        try
        {
            var token = FileOpToken;
            InvalidateList(_directory);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                ShowFileProgress(file.Name, null, done, files.Count);
                var progress = new Progress<int>(value => ShowFileProgress(file.Name, value, done, files.Count));
                await _files.PushAsync(device.Serial, file.Path, _directory.TrimEnd('/') + "/" + file.Name, progress, token);
                done++;
            }
            HideFileProgress();
            await LoadFilesAsync();
            FinishFileOp("已导入 " + done + " 项。");
        }
        catch (OperationCanceledException)
        {
            try
            {
                await LoadFilesAsync();
            }
            catch (Exception)
            {
            }
            FinishFileOp(done > 0 ? "已取消，完成 " + done + "/" + files.Count + " 项。" : "已取消。");
        }
        catch (Exception ex)
        {
            FinishFileOp(ex.Message);
            ShowWarning(ex.Message);
        }
    }

    private async Task<string?> AskAsync(string title, string value)
    {
        var box = new TextBox { Text = value };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private enum PasteChoice { Overwrite, Skip, Cancel }

    private async Task<PasteChoice> AskPasteAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = "粘贴",
            Content = name + " 已存在。",
            PrimaryButtonText = "覆盖",
            SecondaryButtonText = "跳过",
            CloseButtonText = "取消",
            XamlRoot = Root.XamlRoot
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => PasteChoice.Overwrite,
            ContentDialogResult.Secondary => PasteChoice.Skip,
            _ => PasteChoice.Cancel
        };
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = Root.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowWarning(string? text)
    {
        WarningBanner.Message = text ?? "";
        WarningBanner.IsOpen = !string.IsNullOrWhiteSpace(text);
    }

    private void SaveSettings()
    {
        _settings.Draft = _states.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        JsonStore.Save(_paths.Settings, _settings);
    }

    private void RestoreWindow()
    {
        try
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var width = (int)Math.Min(720, work.Width * .5);
            var height = (int)Math.Min(460, work.Height * .55);
            var left = (int)Math.Round(_savedWindow.Left);
            var top = (int)Math.Round(_savedWindow.Top);
            var savedW = (int)Math.Round(_savedWindow.Width);
            var savedH = (int)Math.Round(_savedWindow.Height);
            if (savedW > 0 && savedH > 0 && InsideWorkArea(work, left, top, savedW, savedH))
            {
                AppWindow.Resize(new SizeInt32(savedW, savedH));
                AppWindow.Move(new PointInt32(left, top));
                return;
            }
            width = Math.Clamp(width, 1, Math.Max(1, work.Width));
            height = Math.Clamp(height, 1, Math.Max(1, work.Height));
            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, (work.Height - height) / 2)));
        }
        catch (Exception ex)
        {
            ReportWindowError("RestoreWindow", ex);
        }
    }

    private static bool InsideWorkArea(RectInt32 work, int x, int y, int width, int height) =>
        x >= work.X && y >= work.Y
        && (long)x + width <= (long)work.X + work.Width
        && (long)y + height <= (long)work.Y + work.Height;

    private void SaveWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                return;
            var width = _homeWindowW > 0 && MirrorView.Visibility == Visibility.Visible ? _homeWindowW : AppWindow.Size.Width;
            var height = _homeWindowH > 0 && MirrorView.Visibility == Visibility.Visible ? _homeWindowH : AppWindow.Size.Height;
            JsonStore.Save(_paths.WindowState, new SavedWindowState
            {
                Left = AppWindow.Position.X,
                Top = AppWindow.Position.Y,
                Width = width,
                Height = height
            });
        }
        catch (Exception ex)
        {
            ReportWindowError("SaveWindow", ex);
        }
    }
}
