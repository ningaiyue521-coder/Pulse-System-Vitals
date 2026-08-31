using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DemoApp;

public partial class MiniOverlayWindow : Window
{
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int WmDpiChanged = 0x02E0;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly Brush TemperatureNormalBrush = FrozenBrush(Color.FromRgb(99, 210, 232));
    private static readonly Brush TemperatureWarmBrush = FrozenBrush(Color.FromRgb(255, 179, 64));
    private static readonly Brush TemperatureHotBrush = FrozenBrush(Color.FromRgb(255, 107, 96));
    private bool _allowClose;
    private HwndSource? _source;
    private DateTime _lastLiquidTypographyUpdate = DateTime.MinValue;

    public event EventHandler? RestoreRequested;

    public MiniOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) => EnforceTopmost();
        SizeChanged += (_, _) => ApplyWindowRegion();
        MainGlass.MouseLeftButtonDown += DragOverlay;
        Closing += OnClosing;
        Closed += (_, _) => _source?.RemoveHook(WindowHook);
    }

    public void UpdateSnapshot(SystemSnapshot snapshot)
    {
        MiniCpuValue.Text = $"{snapshot.Cpu:0}%";
        MiniGpuValue.Text = snapshot.GpuAvailable ? $"{snapshot.Gpu:0}%" : "--%";
        MiniMemoryValue.Text = $"{snapshot.MemoryPercent:0}%";
        MiniTemperatureValue.Text = snapshot.TemperatureCelsius is double value ? $"{value:0}°" : "--°";
        MiniTemperatureValue.AccentBrush = snapshot.TemperatureCelsius switch
        {
            >= 88 => TemperatureHotBrush,
            >= 75 => TemperatureWarmBrush,
            _ => TemperatureNormalBrush
        };
        UpdateLiquidTypography(snapshot);
    }

    private void UpdateLiquidTypography(SystemSnapshot snapshot)
    {
        var cpu = Math.Round(snapshot.Cpu);
        var gpu = snapshot.GpuAvailable ? Math.Round(snapshot.Gpu) : 0;
        var memory = Math.Round(snapshot.MemoryPercent);
        var temperature = snapshot.TemperatureCelsius is double value
            ? Math.Round(Math.Clamp((value - 30) / 70 * 100, 0, 100))
            : 0;

        var hasLargeJump = Math.Abs(MiniCpuValue.Value - cpu) >= 6
            || Math.Abs(MiniGpuValue.Value - gpu) >= 6
            || Math.Abs(MiniMemoryValue.Value - memory) >= 6
            || Math.Abs(MiniTemperatureValue.Value - temperature) >= 6;
        if (!hasLargeJump && DateTime.UtcNow - _lastLiquidTypographyUpdate < TimeSpan.FromSeconds(4)) return;

        MiniCpuValue.Value = cpu;
        MiniGpuValue.Value = gpu;
        MiniMemoryValue.Value = memory;
        MiniTemperatureValue.Value = temperature;
        _lastLiquidTypographyUpdate = DateTime.UtcNow;
    }

    internal void ApplyGlassAppearance(GlassPreferences preferences) =>
        GlassPalette.ApplyMini(Resources, preferences);

    public void EnforceTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(extendedStyle | WsExToolWindow | WsExNoActivate));
            _ = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowHook);
        EnforceTopmost();
        _ = Dispatcher.BeginInvoke(ApplyWindowRegion, DispatcherPriority.Loaded);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(Left)) Left = SystemParameters.WorkArea.Right - Width - 22;
        if (double.IsNaN(Top)) Top = SystemParameters.WorkArea.Top + 22;
        ApplyWindowRegion();
        EnforceTopmost();
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDpiChanged)
        {
            _ = Dispatcher.BeginInvoke(ApplyWindowRegion, DispatcherPriority.Loaded);
        }
        return IntPtr.Zero;
    }

    private void ApplyWindowRegion()
    {
        if (!IsLoaded || MainGlass.ActualWidth <= 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var mainRegion = CreateElementRegion(MainGlass, dpi);
        try
        {
            if (mainRegion == IntPtr.Zero) return;
            if (SetWindowRgn(hwnd, mainRegion, true) != 0)
            {
                // Windows owns the region after a successful SetWindowRgn.
                mainRegion = IntPtr.Zero;
            }
        }
        finally
        {
            if (mainRegion != IntPtr.Zero) _ = DeleteObject(mainRegion);
        }
    }

    private IntPtr CreateElementRegion(FrameworkElement element, DpiScale dpi)
    {
        var origin = element.TranslatePoint(new Point(0, 0), this);
        var left = (int)Math.Floor(origin.X * dpi.DpiScaleX);
        var top = (int)Math.Floor(origin.Y * dpi.DpiScaleY);
        var right = (int)Math.Ceiling((origin.X + element.ActualWidth) * dpi.DpiScaleX) + 1;
        var bottom = (int)Math.Ceiling((origin.Y + element.ActualHeight) * dpi.DpiScaleY) + 1;
        var diameter = Math.Max(1, Math.Min(right - left, bottom - top));
        return CreateRoundRectRgn(left, top, right, bottom, diameter, diameter);
    }

    public void Shutdown()
    {
        _allowClose = true;
        RestoreRequested = null;
        Close();
    }

    private void DragOverlay(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || RestoreButton.IsMouseOver) return;
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try { DragMove(); } catch { }
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
