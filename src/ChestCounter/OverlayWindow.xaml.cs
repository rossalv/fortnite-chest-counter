using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ChestCounter;

/// <summary>
/// Always-on-top, click-through counter. It never takes focus, so it's safe to leave over the game
/// (Fortnite must be in Windowed Fullscreen or Windowed mode for any overlay window to appear on top).
/// </summary>
public partial class OverlayWindow : Window
{
    private const int ToggleHotkeyId = 0xC4E5;

    private readonly Settings _settings;
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;
    private bool _moveMode;
    private int? _shownTotal;
    private bool _wasReady;

    /// <summary>Ctrl+Alt+C was pressed (works while the game has focus).</summary>
    public event Action? ToggleRequested;

    /// <summary>The user finished dragging the overlay (right-click or tray menu).</summary>
    public event Action? MoveModeExited;

    public OverlayWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        Left = settings.Left;
        Top = settings.Top;
        ApplyAppearance();

        // Some fullscreen-windowed games push themselves above other topmost windows; re-assert periodically.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => EnsureOnScreen();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonUp += (_, _) => { if (_moveMode) MoveModeExited?.Invoke(); };
        Closed += (_, _) =>
        {
            _topmostTimer.Stop();
            if (_hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(_hwnd, ToggleHotkeyId);
        };
    }

    public bool IsMoveMode => _moveMode;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetExStyle(_hwnd,
            add: NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT,
            remove: 0);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        if (!NativeMethods.RegisterHotKey(_hwnd, ToggleHotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)'C'))
        {
            AppPaths.Log("Ctrl+Alt+C hotkey is already used by another program; use the tray icon instead.");
        }

        _topmostTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            handled = true;
            ToggleRequested?.Invoke();
        }

        return IntPtr.Zero;
    }

    private void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }

    public void ApplyAppearance()
    {
        var scale = Math.Clamp(_settings.Scale, 0.5, 3.0);
        CardScale.ScaleX = scale;
        CardScale.ScaleY = scale;
        Opacity = Math.Clamp(_settings.Opacity, 0.2, 1.0);
        DetailRow.Visibility = _settings.ShowDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>In move mode the overlay accepts the mouse so it can be dragged; otherwise clicks pass through.</summary>
    public void SetMoveMode(bool on)
    {
        _moveMode = on;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetExStyle(_hwnd,
                add: on ? 0 : NativeMethods.WS_EX_TRANSPARENT,
                remove: on ? NativeMethods.WS_EX_TRANSPARENT : 0);
        }

        Card.BorderBrush = on ? Brushes.Gold : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC8, 0x3D));
        Card.BorderThickness = new Thickness(on ? 2 : 1);
        Cursor = on ? Cursors.SizeAll : null;
        RefreshStatus();

        if (!on)
        {
            _settings.Left = Left;
            _settings.Top = Top;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_moveMode && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private string? _statusFromSnapshot;

    public void Display(CounterSnapshot s)
    {
        var culture = CultureInfo.CurrentCulture;

        // Only celebrate real new chests, not the numbers climbing during the first scan.
        var increase = _wasReady && s.Ready && _shownTotal is int prev && s.Total > prev ? s.Total - prev : 0;

        TotalText.Text = s.Total.ToString("N0", culture);
        if (increase > 0)
        {
            Celebrate(increase);
        }

        _shownTotal = s.Total;
        _wasReady = s.Ready;

        if (s.Matches == 0)
        {
            LiveDot.Visibility = Visibility.Collapsed;
            DetailText.Text = s.Pending > 0 ? "Reading your replays…" : "No replays yet";
        }
        else if (s.InMatch)
        {
            // The replay can't be read until the match ends; the total jumps then.
            LiveDot.Visibility = Visibility.Visible;
            DetailText.Text = $"Match in progress  ·  Today {s.Today.ToString("N0", culture)}  ·  Best {s.BestMatch.ToString("N0", culture)}";
        }
        else
        {
            LiveDot.Visibility = Visibility.Collapsed;
            DetailText.Text = $"Last match {s.LastMatch.ToString("N0", culture)}  ·  Today {s.Today.ToString("N0", culture)}  ·  Best {s.BestMatch.ToString("N0", culture)}";
        }

        _statusFromSnapshot = s.Problem
            ?? (s.Pending > 1 ? $"Reading replays… {s.Pending} left" : null);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var text = _moveMode ? "Drag to move · right-click when done" : _statusFromSnapshot;
        StatusText.Text = text ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Little pop on the number plus a fading "+N" when you open a chest.</summary>
    private void Celebrate(int increase)
    {
        var pop = new DoubleAnimation(1.35, 1.0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut },
        };
        TotalScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        TotalScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        PlusText.Text = "+" + increase.ToString(CultureInfo.CurrentCulture);
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.2) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
        PlusText.BeginAnimation(OpacityProperty, fade);
        PlusShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, -6, TimeSpan.FromSeconds(2.2)));
    }

    /// <summary>If a saved position is off every monitor (monitor unplugged, resolution change), bring it back.</summary>
    private void EnsureOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        if (Left < left || Top < top || Left + 40 > right || Top + 20 > bottom)
        {
            Left = SystemParameters.WorkArea.Left + 24;
            Top = SystemParameters.WorkArea.Top + 24;
            _settings.Left = Left;
            _settings.Top = Top;
        }
    }
}
