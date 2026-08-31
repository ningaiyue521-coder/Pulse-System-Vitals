using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DemoApp.Behaviors;

/// <summary>
/// Uses the Windows compositor for the backdrop instead of copying desktop
/// pixels into WPF. Clear Blur Behind preserves the real colour of windows
/// underneath Pulse while DWM keeps the diffusion pass on the GPU.
/// </summary>
public static class GlassyWindowBehavior
{
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable",
            typeof(bool),
            typeof(GlassyWindowBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(AcrylicWindowState),
            typeof(GlassyWindowBehavior),
            new PropertyMetadata(null));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window) return;
        if (e.NewValue is true)
        {
            var state = new AcrylicWindowState(window);
            window.SetValue(StateProperty, state);
            state.Attach();
        }
        else if (window.GetValue(StateProperty) is AcrylicWindowState state)
        {
            state.Dispose();
            window.ClearValue(StateProperty);
        }
    }

    private sealed class AcrylicWindowState : IDisposable
    {
        private readonly Window _window;
        private bool _disposed;

        public AcrylicWindowState(Window window) => _window = window;

        public void Attach()
        {
            _window.SourceInitialized += OnSourceInitialized;
            _window.Loaded += OnLoaded;
            _window.Activated += OnActivated;
            _window.Closed += OnClosed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Loaded -= OnLoaded;
            _window.Activated -= OnActivated;
            _window.Closed -= OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e) => ApplyNativeBackdrop();
        private void OnLoaded(object sender, RoutedEventArgs e) => ApplyNativeBackdrop();
        private void OnActivated(object? sender, EventArgs e) => ApplyNativeBackdrop();
        private void OnClosed(object? sender, EventArgs e) => Dispose();

        private void ApplyNativeBackdrop()
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            var margins = new Margins(-1, -1, -1, -1);
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            ApplyRoundedCorners(hwnd);

            // ACCENT_ENABLE_ACRYLICBLURBEHIND adds a system luminosity layer on
            // Windows 10. Even a nearly transparent tint therefore becomes a
            // flat grey sheet on a large window. Clear Blur Behind keeps the
            // actual backdrop colour visible; Pulse supplies its own very light
            // glass tint and specular highlights in XAML.
            _ = ApplyClearBlur(hwnd);

            // Transparency is a product requirement, not an optional effect.
            // If the undocumented Accent call is unavailable (for example in a
            // remote session), the extended DWM frame remains transparent. It
            // must never fall back to an opaque grey window.
            _window.Background = Brushes.Transparent;
        }

        private static bool ApplyClearBlur(IntPtr hwnd)
        {
            var policy = new AccentPolicy
            {
                AccentState = AccentState.EnableBlurBehind,
                AccentFlags = 0,
                GradientColor = 0,
                AnimationId = 0
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.AccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static void ApplyRoundedCorners(IntPtr hwnd)
        {
            try
            {
                var corner = DwmWindowCornerPreference.Round;
                _ = DwmSetWindowAttribute(
                    hwnd,
                    DwmWindowAttribute.WindowCornerPreference,
                    ref corner,
                    Marshal.SizeOf<DwmWindowCornerPreference>());

                var noColor = DwmColorNone;
                _ = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.BorderColor, ref noColor, sizeof(uint));
                _ = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.CaptionColor, ref noColor, sizeof(uint));
            }
            catch
            {
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        DwmWindowAttribute attribute,
        ref DwmWindowCornerPreference value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        DwmWindowAttribute attribute,
        ref uint value,
        int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public Margins(int left, int right, int top, int bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableBlurBehind = 3
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19
    }

    private enum DwmWindowAttribute
    {
        WindowCornerPreference = 33,
        BorderColor = 34,
        CaptionColor = 35
    }

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    private const uint DwmColorNone = 0xFFFFFFFE;
}
