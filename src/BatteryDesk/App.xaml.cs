using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BatteryDesk;

public partial class App : Application
{
    private Mutex? _mutex;
    private DeviceMonitor? _monitor;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private TrayWindow? _popover;
    private Hardware.KeyboardActivity? _activity;
    private bool _exiting;
    private const string PipeName = "BatteryDesk.Local.Open.v1";
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\BatteryDesk.User.v1", out var first);
        var command = e.Args.Contains("--quit") ? "quit" : e.Args.Contains("--settings") ? "settings" : e.Args.Contains("--show-tray") ? "tray" : "main";
        var captureIndex = Array.IndexOf(e.Args, "--capture");
        string? Option(string name) { var index = Array.IndexOf(e.Args, name); return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null; }
        if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length) command = "capture:" + JsonSerializer.Serialize(new CaptureRequest(Path.GetFullPath(e.Args[captureIndex + 1]), Option("--capture-view") ?? "main", Option("--capture-theme"), int.TryParse(Option("--capture-width"), out var width) ? width : null, int.TryParse(Option("--capture-height"), out var height) ? height : null, Option("--capture-language")));
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(2000);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)); writer.WriteLine(command);
            }
            catch (Exception) { MessageBox.Show(Localizer.T("VoltPerch is already running. Open it from the system tray."), "VoltPerch"); }
            Shutdown(); return;
        }
        try
        {
            _monitor = new DeviceMonitor();
            Localizer.SetLanguage(_monitor.Settings.Language);
            ApplyLanguageResources();
            Localizer.Changed += ApplyLanguageResources;
            _main = new MainWindow(_monitor);
            MainWindow = _main;
            _main.Closing += Closing;
            _tray = new TrayIcon();
            _tray.Clicked += ShowTray;
            _tray.OpenRequested += () => ShowMain(null);
            _tray.RefreshRequested += async () => await _monitor.RefreshAsync();
            _tray.SettingsRequested += () => { ShowMain(null); _main.ShowSettings(); };
            _tray.ExitRequested += ExitApplication;
            _monitor.Updated += (_, _) => Dispatcher.BeginInvoke(new Action(() => _tray?.Update(_monitor)));
            _monitor.LowBattery += (_, devices) => Dispatcher.BeginInvoke(new Action(() =>
                _tray?.NotifyLowBattery(string.Join("; ", devices.Select(d => UiHelpers.DeviceName(d) + " " + d.BatteryPercent + "%")), _monitor.Settings.NotificationSound)));
            _monitor.Start();
            _activity = new Hardware.KeyboardActivity(_monitor.NotifyKeyboardActivity);
            _ = ListenAsync();
            if (!e.Args.Contains("--tray")) ShowMain(null);
            if (command != "main") _ = DispatchCommand(command);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localizer.T("Unable to start VoltPerch:\n{0}", ex.Message), "VoltPerch", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication();
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "data", "last-error.txt"), args.Exception.ToString()); } catch { }
            MessageBox.Show(Localizer.T("The operation could not be completed: {0}", args.Exception.Message), "VoltPerch"); args.Handled = true;
        };
    }
    private void ApplyLanguageResources()
    {
        foreach (var entry in Localizer.Resources) Resources[entry.Key] = entry.Value;
    }
    private async Task ListenAsync()
    {
        while (!_exiting)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync();
                if (command != null) await Dispatcher.InvokeAsync(() => DispatchCommand(command));
            }
            catch (Exception) when (_exiting) { return; }
            catch (Exception) { await Task.Delay(500); }
        }
    }
    private async Task DispatchCommand(string command)
    {
        if (command == "quit") ExitApplication();
        else if (command == "settings") { ShowMain(null); _main!.ShowSettings(); }
        else if (command == "tray") ShowTray();
        else if (command.StartsWith("capture:", StringComparison.Ordinal))
        {
            var request = JsonSerializer.Deserialize<CaptureRequest>(command[8..])!;
            ShowMain(null);
            await _monitor!.RefreshAsync();
            for (var wait = 0; _monitor.IsRefreshing && wait < 100; wait++) await Task.Delay(100);
            var oldTheme = _monitor.Settings.Theme;
            var oldLanguage = _monitor.Settings.Language;
            var oldWidth = _main!.Width;
            var oldHeight = _main.Height;
            ContextMenu? captureMenu = null;
            try
            {
                if (request.Theme is "Dark" or "Light" or "System") _monitor.Settings.Theme = request.Theme;
                if (request.Language is "en" or "zh-CN")
                {
                    _monitor.Settings.Language = request.Language;
                    Localizer.SetLanguage(request.Language);
                }
                if (request.Width is >= 640 and <= 1800) _main.Width = request.Width.Value;
                if (request.Height is >= 460 and <= 1200) _main.Height = request.Height.Value;
                _main.ShowOverview();
                if (request.View.StartsWith("settings", StringComparison.Ordinal))
                {
                    if (request.View == "settings-language-selection")
                    {
                        // Exercise the real selection handler and persistence path, then
                        // restore the user's preferences in the finally block below.
                        var requestedLanguage = request.Language == "zh-CN" ? "zh-CN" : "en";
                        _monitor.Settings.Language = requestedLanguage == "en" ? "zh-CN" : "en";
                        Localizer.SetLanguage(_monitor.Settings.Language);
                        _main.ShowSettings();
                        _main.UpdateLayout();
                        var selector = VisualChildren<ComboBox>(_main.SettingsContent).Single(c =>
                            System.Windows.Automation.AutomationProperties.GetName(c) == Localizer.T("Language"));
                        selector.SelectedValue = requestedLanguage;
                        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(
                            Path.Combine(_monitor.DataDirectory, "settings.json")))!;
                        var verified = saved.Language == requestedLanguage && Localizer.Language == requestedLanguage
                            && _main.PageTitle.Text == Localizer.T("Settings");
                        File.WriteAllText(request.Path + ".json", JsonSerializer.Serialize(new
                        {
                            RequestedLanguage = requestedLanguage,
                            SavedLanguage = saved.Language,
                            UiLanguage = Localizer.Language,
                            SelectionHandlerVerified = verified
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    else if (request.View == "settings-theme-switch")
                    {
                        // Build once in the opposite palette, then change only resources.
                        // This catches controls that captured a stale foreground brush.
                        _monitor.Settings.Theme = request.Theme == "Dark" ? "Light" : "Dark";
                        UiHelpers.Theme(_main, _monitor.Settings);
                        _main.ShowSettings();
                        _monitor.Settings.Theme = request.Theme ?? oldTheme;
                        UiHelpers.Theme(_main, _monitor.Settings);
                    }
                    else _main.ShowSettings();
                    if (request.View is "settings-bottom" or "settings-theme-switch")
                    {
                        _main.UpdateLayout();
                        _main.SettingsScroll.ScrollToEnd();
                    }
                }
                if (request.View is "keyboard" or "mouse" or "controller" or "headphones")
                {
                    var kind = request.View switch { "keyboard" => DeviceKind.Keyboard, "controller" => DeviceKind.Controller, "headphones" => DeviceKind.Headphones, _ => DeviceKind.Mouse };
                    var device = _monitor.Devices.FirstOrDefault(d => d.Kind == kind);
                    if (device != null) _main.ShowDevice(device.Id);
                }
                FrameworkElement target = _main;
                if (request.View is "tray" or "tray-all" or "tray-menu")
                {
                    ShowTray(); target = _popover!;
                    if (request.View == "tray-all") _popover!.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                if (request.View is "menu" or "tray-menu")
                {
                    var rows = request.View == "menu" ? _main.DeviceRows : _popover!.Rows;
                    var row = rows.Children.OfType<Border>().Select(b => b.Child).OfType<Button>().FirstOrDefault();
                    if (row?.ContextMenu is ContextMenu menu)
                    {
                        captureMenu = menu; menu.PlacementTarget = row;
                        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                        menu.IsOpen = true; target = menu;
                    }
                }
                await Task.Delay(600);
                target.UpdateLayout();
                var visual = target is Window window ? (FrameworkElement)window.Content : target;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth + visual.Margin.Left + visual.Margin.Right), (int)Math.Ceiling(visual.ActualHeight + visual.Margin.Top + visual.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(target);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(request.Path); encoder.Save(file);
            }
            finally
            {
                if (captureMenu != null) captureMenu.IsOpen = false;
                _monitor.Settings.Theme = oldTheme; _main.Width = oldWidth; _main.Height = oldHeight;
                _monitor.Settings.Language = oldLanguage; Localizer.SetLanguage(oldLanguage);
                if (request.View == "settings-language-selection") _monitor.SaveSettings();
                UiHelpers.Theme(_main, _monitor.Settings);
                if (request.View.StartsWith("settings", StringComparison.Ordinal)) _main.ShowSettings();
            }
        }
        else ShowMain(null);
    }
    private static System.Collections.Generic.IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private void ShowMain(string? deviceId)
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        if (deviceId != null) _main.ShowDevice(deviceId);
    }
    private void ShowTray()
    {
        if (_popover?.IsVisible == true) { _popover.Close(); return; }
        _popover = new TrayWindow(_monitor!, ShowMain);
        _popover.SettingsRequested += (_, _) => { ShowMain(null); _main!.ShowSettings(); };
        _popover.RemoveRequested += async id => { _popover?.Close(); ShowMain(null); await _main!.RequestRemoveDeviceAsync(id); };
        _popover.Closed += (_, _) => _popover = null;
        _popover.ShowAtTray(_tray?.GetBounds());
    }
    private void Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_monitor!.Settings.CloseToTray) { e.Cancel = true; _main!.Hide(); }
        else ExitApplication();
    }
    private void ExitApplication() { _exiting = true; _popover?.Close(); _monitor?.Dispose(); _tray?.Dispose(); _tray = null; Shutdown(); }
    protected override void OnExit(ExitEventArgs e) { _activity?.Dispose(); _monitor?.Dispose(); _tray?.Dispose(); _mutex?.Dispose(); base.OnExit(e); }
    private sealed record CaptureRequest(string Path, string View, string? Theme, int? Width, int? Height, string? Language);
}
