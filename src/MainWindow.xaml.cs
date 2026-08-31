using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DemoApp;

public partial class MainWindow : Window
{
    private readonly SystemMetricsSampler _sampler = new();
    private readonly ObservableCollection<ProcessRow> _processes = [];
    private readonly List<double> _cpuHistory = [];
    private readonly List<double> _memoryHistory = [];
    private readonly GlassPreferences _glassPreferences = GlassPreferences.Load();
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _appearanceUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private MiniOverlayWindow? _miniOverlay;
    private SystemSnapshot? _latestSnapshot;
    private bool _paused;
    private bool _switchingWindowMode;
    private bool _glassControlsReady;
    private bool _glassAppearanceDirty;
    private double _lightX = 0.22;
    private double _lightY = 0.10;
    private DateTime _lastLiquidGaugeUpdate = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        ProcessList.ItemsSource = _processes;

        GlassTransparencySlider.Value = _glassPreferences.Transparency;
        GlassRefractionSlider.Value = _glassPreferences.Refraction;
        _glassControlsReady = true;
        ApplyGlassAppearance();
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            _glassPreferences.Save();
        };
        _appearanceUpdateTimer.Tick += (_, _) =>
        {
            if (!_glassAppearanceDirty)
            {
                _appearanceUpdateTimer.Stop();
                return;
            }

            _glassAppearanceDirty = false;
            ApplyGlassAppearance();
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        MouseMove += OnWindowMouseMove;
        PreviewKeyDown += OnPreviewKeyDown;
        ChartCanvas.SizeChanged += (_, _) => DrawChart();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MachineNameText.Text = Environment.MachineName;
        OsText.Text = SystemMetricsSampler.OperatingSystemLabel;
        CoreValue.Text = Environment.ProcessorCount.ToString();
        UpdateRefractionStatus();
        _sampler.SnapshotReady += OnSnapshotReady;
        _sampler.SamplingFailed += OnSamplingFailed;
        _sampler.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _settingsSaveTimer.Stop();
        _appearanceUpdateTimer.Stop();
        _glassPreferences.Save();
        _sampler.SnapshotReady -= OnSnapshotReady;
        _sampler.SamplingFailed -= OnSamplingFailed;
        _sampler.Dispose();
        _miniOverlay?.Shutdown();
        _miniOverlay = null;
    }

    private void GlassSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_glassControlsReady) return;

        _glassPreferences.Transparency = GlassTransparencySlider.Value;
        _glassPreferences.Refraction = GlassRefractionSlider.Value;
        GlassTransparencyValue.Text = $"{_glassPreferences.Transparency:0}%";
        GlassRefractionValue.Text = $"{_glassPreferences.Refraction:0}%";
        QueueGlassAppearanceUpdate();

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void QueueGlassAppearanceUpdate()
    {
        _glassAppearanceDirty = true;
        if (!_appearanceUpdateTimer.IsEnabled) _appearanceUpdateTimer.Start();
    }

    private void ApplyGlassAppearance()
    {
        GlassTransparencyValue.Text = $"{_glassPreferences.Transparency:0}%";
        GlassRefractionValue.Text = $"{_glassPreferences.Refraction:0}%";
        GlassPalette.ApplyMain(Resources, _glassPreferences, _lightX, _lightY);
        UpdateRefractionStatus();
        _miniOverlay?.ApplyGlassAppearance(_glassPreferences);
    }

    private void UpdateRefractionStatus()
    {
        if (RefractionStatusText is null) return;
        var value = _glassPreferences.Refraction;
        RefractionStatusText.Text = value switch
        {
            <= 0.5 => "已关闭 · 只保留基础玻璃轮廓",
            < 35 => "轻微 · 柔和内缘与镜面高光",
            < 70 => "清晰 · 内缘、色散与高光联动",
            _ => "强烈 · 明亮内缘，无暗边与黑边"
        };
        RefractionStatusText.ToolTip = "控制安全的内侧高光、轻微色散和边缘厚度，不再重采样背景像素";
        System.Windows.Automation.AutomationProperties.SetHelpText(
            RefractionStatusText,
            "Safe optical edge renderer; background resampling is disabled");
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_glassPreferences.Refraction < 1 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var point = e.GetPosition(this);
        var x = Math.Clamp(point.X / ActualWidth, 0, 1);
        var y = Math.Clamp(point.Y / ActualHeight, 0, 1);
        if (Math.Abs(x - _lightX) < 0.025 && Math.Abs(y - _lightY) < 0.025) return;

        _lightX = x;
        _lightY = y;
        QueueGlassAppearanceUpdate();
    }

    private void GlassSettings_Click(object sender, RoutedEventArgs e)
    {
        GlassSettingsLayer.Visibility = GlassSettingsLayer.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void GlassSettingsLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, GlassSettingsLayer))
        {
            GlassSettingsLayer.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetGlassSettings_Click(object sender, RoutedEventArgs e)
    {
        GlassTransparencySlider.Value = GlassPreferences.DefaultTransparency;
        GlassRefractionSlider.Value = GlassPreferences.DefaultRefraction;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || GlassSettingsLayer.Visibility != Visibility.Visible) return;
        GlassSettingsLayer.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnSamplingFailed(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            FooterText.Text = $"采样器已降级：{message}";
            LiveStatus.Text = "部分指标不可用";
            LiveDot.Fill = new SolidColorBrush(Color.FromRgb(255, 159, 10));
        });
    }

    private void OnSnapshotReady(SystemSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            _latestSnapshot = snapshot;
            _miniOverlay?.UpdateSnapshot(snapshot);
            if (IsVisible && WindowState != WindowState.Minimized) ApplySnapshot(snapshot);
        });
    }

    private void ApplySnapshot(SystemSnapshot snapshot)
    {
        CpuValue.Text = $"{snapshot.Cpu:0}%";
        MemoryValue.Text = $"{snapshot.MemoryPercent:0}%";
        MemoryDetail.Text = $"{snapshot.MemoryUsedGb:0.0} / {snapshot.MemoryTotalGb:0.0} GB";

        DiskValue.Text = $"{snapshot.DiskUsedPercent:0}%";
        DiskDetail.Text = $"剩余 {snapshot.DiskFreeGb:0} GB · {FormatRate(snapshot.DiskBytesPerSecond)}";

        if (snapshot.GpuAvailable)
        {
            GpuValue.Text = $"{snapshot.Gpu:0}%";
            GpuDetail.Text = string.IsNullOrWhiteSpace(snapshot.GpuName) ? "GPU Engine" : snapshot.GpuName;
        }
        else
        {
            GpuValue.Text = "--";
            GpuDetail.Text = "此驱动未开放计数器";
        }

        if (snapshot.TemperatureCelsius is double temperature)
        {
            TemperatureValue.Text = $"{temperature:0}°";
            TemperatureDetail.Text = "ACPI 热区传感器";
            SensorStateText.Text = "温度可用";
        }
        else
        {
            TemperatureValue.Text = "--°";
            TemperatureDetail.Text = "主板未向 Windows 开放";
            SensorStateText.Text = snapshot.GpuAvailable ? "基础 + GPU" : "基础指标";
        }

        UpdateLiquidGauges(snapshot);

        NetworkValue.Text = FormatRate(snapshot.DownloadBytesPerSecond + snapshot.UploadBytesPerSecond);
        NetworkDetail.Text = $"↓ {FormatRate(snapshot.DownloadBytesPerSecond)}   ↑ {FormatRate(snapshot.UploadBytesPerSecond)}";
        UptimeValue.Text = FormatUptime(snapshot.Uptime);
        ClockText.Text = snapshot.Timestamp.ToString("yyyy年M月d日  HH:mm:ss");
        FooterText.Text = "iOS 27 功能玻璃层 · 清晰内容面 · 无常驻动画";
        System.Windows.Automation.AutomationProperties.SetHelpText(
            FooterText,
            $"最新采样耗时 {snapshot.SampleDuration.TotalMilliseconds:0.0} ms");

        _cpuHistory.Add(snapshot.Cpu);
        _memoryHistory.Add(snapshot.MemoryPercent);
        if (_cpuHistory.Count > 30) _cpuHistory.RemoveAt(0);
        if (_memoryHistory.Count > 30) _memoryHistory.RemoveAt(0);
        DrawChart();
        UpdateHealth(snapshot);

        if (snapshot.Processes is not null)
        {
            _processes.Clear();
            foreach (var process in snapshot.Processes)
            {
                _processes.Add(process);
            }

            ProcessCountText.Text = $"{snapshot.ProcessCount} 个运行中";
        }
    }

    private void UpdateLiquidGauges(SystemSnapshot snapshot)
    {
        var cpu = Math.Round(snapshot.Cpu);
        var memory = Math.Round(snapshot.MemoryPercent);
        var disk = Math.Round(snapshot.DiskUsedPercent);
        var gpu = snapshot.GpuAvailable ? Math.Round(snapshot.Gpu) : 0;
        var temperature = snapshot.TemperatureCelsius is double value
            ? Math.Round(Math.Clamp(value, 0, 100))
            : 0;

        var due = snapshot.Timestamp - _lastLiquidGaugeUpdate >= TimeSpan.FromSeconds(4);
        var largeJump = Math.Abs(CpuLiquidGauge.Value - cpu) >= 6
            || Math.Abs(MemoryBar.Value - memory) >= 6
            || Math.Abs(DiskBar.Value - disk) >= 6
            || Math.Abs(GpuBar.Value - gpu) >= 6
            || Math.Abs(TemperatureBar.Value - temperature) >= 6;
        if (!due && !largeJump) return;

        CpuLiquidGauge.Value = cpu;
        MemoryBar.Value = memory;
        DiskBar.Value = disk;
        GpuBar.Value = gpu;
        TemperatureBar.Value = temperature;
        _lastLiquidGaugeUpdate = snapshot.Timestamp;
    }

    private void UpdateHealth(SystemSnapshot snapshot)
    {
        var pressure = Math.Max(snapshot.Cpu, Math.Max(snapshot.MemoryPercent, snapshot.Gpu));
        if (pressure < 55)
        {
            HealthText.Text = "系统平静，所有核心指标正常";
            HealthBadge.Text = "CALM";
            HealthBadge.Foreground = new SolidColorBrush(Color.FromRgb(30, 99, 116));
            CpuLiquidGauge.Foreground = (Brush)FindResource("Blue");
        }
        else if (pressure < 80)
        {
            HealthText.Text = "系统正在专注处理任务";
            HealthBadge.Text = "FLOW";
            HealthBadge.Foreground = (Brush)FindResource("Violet");
            CpuLiquidGauge.Foreground = (Brush)FindResource("Violet");
        }
        else
        {
            HealthText.Text = "负载较高，建议关注活动进程";
            HealthBadge.Text = "HOT";
            HealthBadge.Foreground = new SolidColorBrush(Color.FromRgb(215, 91, 45));
            CpuLiquidGauge.Foreground = (Brush)FindResource("Orange");
        }
    }

    private void DrawChart()
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        CpuLine.Points = MakePoints(_cpuHistory, width, height);
        MemoryLine.Points = MakePoints(_memoryHistory, width, height);
    }

    private static PointCollection MakePoints(IReadOnlyList<double> values, double width, double height)
    {
        var points = new PointCollection(values.Count);
        var denominator = Math.Max(1, values.Count - 1);
        for (var index = 0; index < values.Count; index++)
        {
            var x = index * width / denominator;
            var y = height - 4 - Math.Clamp(values[index], 0, 100) / 100.0 * (height - 8);
            points.Add(new Point(x, y));
        }
        return points;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        _sampler.Paused = _paused;
        PauseButton.Content = _paused ? "继续" : "暂停";
        LiveStatus.Text = _paused ? "监测已暂停" : "实时监测";
        LiveDot.Fill = new SolidColorBrush(_paused ? Color.FromRgb(255, 159, 10) : Color.FromRgb(48, 209, 88));
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_switchingWindowMode || WindowState != WindowState.Minimized) return;
        GlassSettingsLayer.Visibility = Visibility.Collapsed;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_switchingWindowMode) return;
            _switchingWindowMode = true;
            WindowState = WindowState.Normal;
            Hide();
            ShowMiniOverlay();
            _switchingWindowMode = false;
        });
    }

    private void ShowMiniOverlay()
    {
        _sampler.CompactMode = true;
        if (_miniOverlay is null)
        {
            _miniOverlay = new MiniOverlayWindow();
            _miniOverlay.RestoreRequested += RestoreFromMiniOverlay;
        }

        _miniOverlay.ApplyGlassAppearance(_glassPreferences);
        if (_latestSnapshot is not null) _miniOverlay.UpdateSnapshot(_latestSnapshot);
        if (!_miniOverlay.IsVisible) _miniOverlay.Show();
        _miniOverlay.EnforceTopmost();
    }

    private void RestoreFromMiniOverlay(object? sender, EventArgs e)
    {
        _sampler.CompactMode = false;
        _sampler.RequestProcessRefresh();
        _miniOverlay?.Hide();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int processId } || processId <= 4 || processId == Environment.ProcessId) return;

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            var answer = MessageBox.Show(
                this,
                $"确定结束 {processName}（PID {processId}）吗？\n未保存的数据可能会丢失。",
                "结束进程",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            process.Kill(entireProcessTree: true);
            _sampler.RequestProcessRefresh();
            FooterText.Text = $"已请求结束 {processName}";
        }
        catch (ArgumentException)
        {
            FooterText.Text = "该进程已经结束";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法结束该进程：{exception.Message}", "Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024 * 1024) return $"{bytesPerSecond / (1024 * 1024 * 1024):0.0} GB/s";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0} KB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1) return $"{(int)uptime.TotalDays}天 {uptime.Hours}时";
        if (uptime.TotalHours >= 1) return $"{(int)uptime.TotalHours}时 {uptime.Minutes}分";
        return $"{uptime.Minutes}分";
    }
}
