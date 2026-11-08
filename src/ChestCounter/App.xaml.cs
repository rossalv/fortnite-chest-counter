using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ChestCounter;

public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "FortniteChestCounter";

    private Mutex? _singleInstance;
    private Settings _settings = new();
    private ChestTracker? _tracker;
    private OverlayWindow? _overlay;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _headerItem, _showItem, _moveItem, _detailsItem, _startupItem;
    private WinForms.ToolStripMenuItem[] _sizeItems = Array.Empty<WinForms.ToolStripMenuItem>();
    private System.Drawing.Icon? _icon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mutexName = AppPaths.IsCustomDataFolder
            ? @"Local\FortniteChestCounter-" + (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(AppPaths.DataFolder)
            : @"Local\FortniteChestCounter";
        _singleInstance = new Mutex(true, mutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Fortnite Chest Counter is already running (look for the chest icon in the system tray).",
                "Fortnite Chest Counter", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppPaths.Log("Fatal: " + args.ExceptionObject);

        _settings = Settings.Load();

        _overlay = new OverlayWindow(_settings);
        _overlay.ToggleRequested += ToggleOverlay;
        _overlay.MoveModeExited += () => SetMoveMode(false);
        if (!_settings.Hidden)
        {
            _overlay.Show();
        }

        CreateTray();

        _tracker = new ChestTracker(_settings);
        _tracker.Updated += snapshot => Dispatcher.BeginInvoke(() => OnSnapshot(snapshot));
        _tracker.Start();
    }

    private void OnSnapshot(CounterSnapshot snapshot)
    {
        _overlay?.Display(snapshot);

        var culture = CultureInfo.CurrentCulture;
        var since = _settings.CountFrom is { } from ? $" since {from.ToLocalTime().ToString("MMM d, yyyy", culture)}" : "";
        if (_headerItem != null)
        {
            _headerItem.Text = $"{snapshot.Total.ToString("N0", culture)} chests{since}  ({snapshot.Matches} matches)";
        }

        if (_tray != null)
        {
            var tip = $"{snapshot.Total.ToString("N0", culture)} chests{since}";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        }
    }

    private void CreateTray()
    {
        _icon = TrayIconArt.Create();

        var menu = new WinForms.ContextMenuStrip();
        _headerItem = new WinForms.ToolStripMenuItem("Chests opened: …") { Enabled = false };
        _showItem = new WinForms.ToolStripMenuItem("Show overlay", null, (_, _) => ToggleOverlay())
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+C",
            Checked = !_settings.Hidden,
        };
        _moveItem = new WinForms.ToolStripMenuItem("Move overlay", null, (_, _) => SetMoveMode(!(_overlay?.IsMoveMode ?? false)));
        _detailsItem = new WinForms.ToolStripMenuItem("Show match / today line", null, (_, _) =>
        {
            _settings.ShowDetails = !_settings.ShowDetails;
            _detailsItem!.Checked = _settings.ShowDetails;
            _overlay?.ApplyAppearance();
            _settings.Save();
        })
        { Checked = _settings.ShowDetails };

        var sizeMenu = new WinForms.ToolStripMenuItem("Size");
        var sizes = new (string Name, double Scale)[] { ("Small", 0.8), ("Normal", 1.0), ("Large", 1.3), ("Extra large", 1.7) };
        _sizeItems = new WinForms.ToolStripMenuItem[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var (name, scale) = sizes[i];
            var item = new WinForms.ToolStripMenuItem(name, null, (_, _) =>
            {
                _settings.Scale = scale;
                _overlay?.ApplyAppearance();
                UpdateSizeChecks();
                _settings.Save();
            });
            _sizeItems[i] = item;
            item.Tag = scale;
            sizeMenu.DropDownItems.Add(item);
        }

        UpdateSizeChecks();

        _startupItem = new WinForms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled(),
        };

        menu.Items.Add(_headerItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_showItem);
        menu.Items.Add(_moveItem);
        menu.Items.Add(sizeMenu);
        menu.Items.Add(_detailsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Rescan all replays", null, (_, _) => _tracker?.RequestRescan()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open replays folder", null, (_, _) => OpenFolder(_settings.ResolvedDemosFolder)));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open settings folder", null, (_, _) => OpenFolder(AppPaths.DataFolder)));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "Fortnite Chest Counter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ToggleOverlay();
    }

    private void UpdateSizeChecks()
    {
        foreach (var item in _sizeItems)
        {
            item.Checked = item.Tag is double scale && Math.Abs(scale - _settings.Scale) < 0.01;
        }
    }

    private void ToggleOverlay()
    {
        if (_overlay is null)
        {
            return;
        }

        if (_overlay.IsVisible)
        {
            SetMoveMode(false);
            _overlay.Hide();
        }
        else
        {
            _overlay.Show();
        }

        _settings.Hidden = !_overlay.IsVisible;
        if (_showItem != null) _showItem.Checked = _overlay.IsVisible;
        _settings.Save();
    }

    private void SetMoveMode(bool on)
    {
        if (_overlay is null)
        {
            return;
        }

        if (on && !_overlay.IsVisible)
        {
            ToggleOverlay();
        }

        _overlay.SetMoveMode(on);
        if (_moveItem != null) _moveItem.Checked = on;
        if (!on)
        {
            _settings.Save();
        }
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (IsStartupEnabled())
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            else if (Environment.ProcessPath is { } exe)
            {
                key.SetValue(RunValue, $"\"{exe}\"");
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not change startup setting: " + ex.Message);
        }

        if (_startupItem != null) _startupItem.Checked = IsStartupEnabled();
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not open folder: " + ex.Message);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("UI error: " + e.Exception);
        e.Handled = true; // keep the overlay alive
    }

    private void ExitApp()
    {
        if (_overlay != null)
        {
            if (_overlay.IsMoveMode) _overlay.SetMoveMode(false);
            _settings.Save();
            _overlay.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tracker?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _icon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
