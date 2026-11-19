using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BatteryDesk;

public partial class MainWindow : Window
{
    private readonly DeviceMonitor _monitor;
    private string _filter = "All";
    private string? _selectedId;
    private bool _settingsPage;
    private bool _compact;
    private bool _buildingSettings;
    private ComboBox? _trayDeviceChoice;
    private Expander? _batterySupport;
    private string? _batterySupportDeviceId;
    private ContextMenu? _deviceMenu;
    public MainWindow(DeviceMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        UiHelpers.Theme(this, _monitor.Settings);
        _monitor.Updated += MonitorUpdated;
        Loaded += (_, _) => { UpdateWindowLayout(); Render(); };
        Closed += (_, _) => _monitor.Updated -= MonitorUpdated;
        Viewport.SizeChanged += (_, _) => UpdateWindowLayout();
        SettingsScroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) UpdateSettingsLayout(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_settingsPage) BackClick(this, e); else CloseDetailsClick(this, e); e.Handled = true; } };
    }
    private void UpdateWindowLayout()
    {
        if (Viewport.ActualWidth <= 0) return;
        bool compact = Viewport.ActualWidth < 810;
        if (_compact != compact)
        {
            _compact = compact;
            UpdateDetailLayout();
            if (_compact && _selectedId != null && !_settingsPage && !DetailFrame.IsKeyboardFocusWithin)
                CloseDetailButton.Focus();
        }
        UpdateSettingsLayout();
    }
    private void MonitorUpdated(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(Render));
    }
    private void Render()
    {
        // Keep the row owning an open menu alive during automatic refreshes.
        if (_deviceMenu != null) return;
        UiHelpers.Theme(this, _monitor.Settings);
        RefreshButton.IsEnabled = !_monitor.IsRefreshing;
        RefreshLabel.Text = _monitor.IsRefreshing ? UiHelpers.T("Reading...") : UiHelpers.T("Refresh");
        StatusLabel.Text = Localizer.Translate(_monitor.StatusText);
        var all = _monitor.Devices;
        if (_settingsPage) UpdateTrayDeviceChoice(all);
        if (_selectedId != null && !all.Any(d => d.Id == _selectedId)) _selectedId = null;
        if (_settingsPage) return;
        PageSubtitle.Text = UiHelpers.T("{0} devices · {1} connected", all.Count, all.Count(d => d.Connection == LinkState.Connected));
        var focusedRow = (Keyboard.FocusedElement as Button)?.Tag as string;
        DeviceRows.Children.Clear();
        var devices = all.Where(d => _filter == "All" || (_filter == "Connected" ? d.Connection == LinkState.Connected : d.Connection == LinkState.Disconnected)).ToArray();
        EmptyPanel.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = UiHelpers.T(all.Count == 0 ? "Loc.NoDevices" : "No devices match this filter.");
        EmptyText.Text = all.Count == 0
            ? UiHelpers.T(_monitor.IsRefreshing ? "Finding devices and reading battery levels..." : "Connect a device or plug in its receiver, then refresh.")
            : UiHelpers.T("Choose All devices to see the full list.");
        foreach (var device in devices) DeviceRows.Children.Add(CreateDeviceRow(device));
        UpdateDetailLayout();
        if (_selectedId != null) RenderDetails(all.First(d => d.Id == _selectedId));
        if (focusedRow != null && DeviceListFrame.IsVisible && !FocusDeviceRow(focusedRow)) FocusFilter();
    }
    private bool FocusDeviceRow(string id) => DeviceRows.Children.OfType<Border>()
        .Select(row => row.Child).OfType<Button>().FirstOrDefault(button => Equals(button.Tag, id))?.Focus() == true;

    private void FocusFilter() => (_filter switch { "Connected" => ConnectedFilter, "Disconnected" => OfflineFilter, _ => AllFilter }).Focus();
    private UIElement CreateDeviceRow(DeviceSnapshot device)
    {
        const double batteryWidth = 190;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(batteryWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        var icon = UiHelpers.Icon(device.Kind, 42, device.Name);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(icon);

        var title = UiHelpers.Text(UiHelpers.DeviceName(device), 16);
        title.FontWeight = FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.ToolTip = title.Text;
        var connection = ConnectionLine(device, Localizer.Translate(device.Transport));
        connection.Margin = new Thickness(0, 5, 0, 0);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        identity.Children.Add(title);
        identity.Children.Add(connection);
        Grid.SetColumn(identity, 1);
        grid.Children.Add(identity);

        UIElement battery;
        if (UiHelpers.HasSeparateAirPodsComponents(device))
        {
            battery = UiHelpers.AirPodsComponentPanel(device, batteryWidth, 11, 15, 9, 11, _monitor.Settings, compactCaption: true);
        }
        else
        {
            var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var number = UiHelpers.Text(UiHelpers.BatteryText(device), 25, !device.IsLive ? "MutedBrush" : UiHelpers.Low(device, _monitor.Settings) ? "LowBrush" : "TextBrush");
            number.FontWeight = FontWeights.SemiBold;
            number.TextAlignment = TextAlignment.Right;
            summary.Children.Add(number);
            var caption = UiHelpers.Text(UiHelpers.BatteryCaption(device, _monitor.Settings), 12, UiHelpers.Low(device, _monitor.Settings) ? "LowBrush" : "MutedBrush");
            caption.TextAlignment = TextAlignment.Right;
            summary.Children.Add(caption);
            battery = summary;
        }
        Grid.SetColumn(battery, 2);
        grid.Children.Add(battery);
        var chevron = UiHelpers.Text("›", 20, "MutedBrush");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);
        var button = new Button { Content = grid, Style = (Style)FindResource("RowButton"), Tag = device.Id, Padding = new Thickness(10, 11, 10, 11), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
        if (device.Id == _selectedId)
        {
            if (!SystemParameters.HighContrast) button.SetResourceReference(BackgroundProperty, "HoverBrush");
            button.SetResourceReference(BorderBrushProperty, "AccentBrush");
        }
        AutomationProperties.SetName(button, UiHelpers.T("{0}, {1}, {2}, {3}",UiHelpers.DeviceName(device),UiHelpers.ConnectionText(device),Localizer.Translate(device.Transport),UiHelpers.BatteryAccessibilityText(device,_monitor.Settings)));
        button.Click += (_, _) => { ShowDevice(device.Id); CloseDetailButton.Focus(); };
        var menu = new ContextMenu { Style = (Style)FindResource("DeviceContextMenu") };
        var removeItem = new MenuItem { Header = UiHelpers.T("Remove device"), Style = (Style)FindResource("DeviceContextMenuItem") };
        AutomationProperties.SetName(removeItem, UiHelpers.T("Remove {0} from the device list",UiHelpers.DeviceName(device)));
        removeItem.Click += async (_,_) => { menu.IsOpen = false; await ConfirmRemoveDeviceAsync(device); };
        menu.Items.Add(removeItem);
        foreach (var target in _monitor.Devices.Where(target => DeviceMonitor.CanMergeKeyboard(device, target)))
        {
            var merge = new MenuItem { Header = UiHelpers.T("Merge into {0}", UiHelpers.DeviceName(target)), Style = (Style)FindResource("DeviceContextMenuItem") };
            merge.Click += async (_,_) => { menu.IsOpen = false; await ConfirmMergeKeyboardAsync(device, target); };
            menu.Items.Add(merge);
        }
        button.ContextMenuOpening += (_,_) => _deviceMenu = menu;
        menu.Opened += (_,_) =>
        {
            _deviceMenu = menu;
            foreach (var key in new[] { "TextBrush", "HoverBrush", "AccentBrush" }) menu.Resources[key] = UiHelpers.Brush(this, key);
            menu.Background = UiHelpers.Brush(this, "SurfaceBrush");
            menu.Foreground = UiHelpers.Brush(this, "TextBrush");
            menu.BorderBrush = UiHelpers.Brush(this, "LineBrush");
        };
        menu.Closed += (_,_) => { if (_deviceMenu == menu) _deviceMenu = null; Dispatcher.BeginInvoke(new Action(Render)); };
        button.ContextMenu = menu;
        var wrapper = new Border { Child=button, BorderThickness=new Thickness(0,0,0,1) };wrapper.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
        return wrapper;
    }
    private static DockPanel ConnectionLine(DeviceSnapshot device, string? transport = null)
    {
        var panel = new DockPanel();
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, device.Connection == LinkState.Connected ? "GoodBrush" : "MutedBrush");
        panel.Children.Add(dot);
        var text = UiHelpers.ConnectionText(device);
        if (transport != null) text += "  /  " + transport;
        var label = UiHelpers.Text(text, 12, "MutedBrush");
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.ToolTip = text;
        panel.Children.Add(label);
        return panel;
    }
    public void ShowDevice(string id)
    {
        if (_selectedId != id) DetailScroller.ScrollToTop();
        _selectedId = id;
        _settingsPage = false;
        ShowDevicesPage();
        Render();
    }
    public void ShowSettings()
    {
        _settingsPage = true;
        PageTitle.Text = UiHelpers.T("Settings");
        PageSubtitle.Text = UiHelpers.T("Make it work your way");
        BackButton.Visibility = Visibility.Visible;
        DeviceToolbar.Visibility = Visibility.Collapsed;
        DevicesLayout.Visibility = Visibility.Collapsed;
        SettingsScroll.Visibility = Visibility.Visible;
        BuildSettings();
    }
    public void ShowOverview() { _selectedId = null; _settingsPage = false; ShowDevicesPage(); Render(); }
    private void ShowDevicesPage()
    {
        PageTitle.Text = UiHelpers.T("Loc.MainTitle");
        BackButton.Visibility = Visibility.Collapsed;
        DeviceToolbar.Visibility = Visibility.Visible;
        DevicesLayout.Visibility = Visibility.Visible;
        SettingsScroll.Visibility = Visibility.Collapsed;
    }
    private void UpdateDetailLayout()
    {
        bool show = _selectedId != null;
        bool singlePane = show && _compact;
        DetailFrame.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DeviceListFrame.Visibility = singlePane ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(DetailFrame, singlePane ? 0 : 1);
        DetailColumn.Width = new GridLength(show && !singlePane ? 356 : 0);
        DetailFrame.Margin = new Thickness(singlePane ? 0 : 14, 0, 0, 0);
    }
    private void RenderDetails(DeviceSnapshot? d)
    {
        DetailsContent.Children.Clear(); if(d==null)return;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = UiHelpers.Icon(d.Kind, 44, d.Name);
        icon.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(icon);
        var identity = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(identity, 1);
        var name = UiHelpers.Text(UiHelpers.DeviceName(d), 19);
        name.FontWeight = FontWeights.SemiBold;
        identity.Children.Add(name);
        var statusLine = ConnectionLine(d, UiHelpers.KindName(d.Kind));
        statusLine.Margin = new Thickness(0, 4, 0, 0);
        identity.Children.Add(statusLine);
        header.Children.Add(identity);
        DetailsContent.Children.Add(header);

        var summary = UiHelpers.BatteryText(d);
        var summaryLive = d.IsLive;
        var summaryLow = UiHelpers.Low(d,_monitor.Settings);
        string? componentLabel = null;
        if (d.BatteryComponents is { } airPods)
        {
            var leftIsLower = airPods.Left is int left && (airPods.Right is not int right || left <= right);
            var earbud = leftIsLower ? airPods.Left : airPods.Right;
            var percent = earbud ?? airPods.Case;
            var charging = leftIsLower ? airPods.LeftCharging : airPods.Right.HasValue ? airPods.RightCharging : airPods.CaseCharging;
            componentLabel = airPods.SingleBattery ? UiHelpers.T("Headphone battery")
                : earbud.HasValue ? UiHelpers.T("Lowest earbud") : UiHelpers.T("Charging case");
            summary = UiHelpers.BatteryComponentValueText(percent);
            summaryLive = d.IsLive && UiHelpers.HasRecentAirPodsBroadcast(d);
            summaryLow = !charging && percent <= _monitor.Settings.LowBatteryThreshold;
        }

        var battery = new StackPanel();
        if (componentLabel != null) battery.Children.Add(UiHelpers.Text(componentLabel, 13, "MutedBrush"));
        var value = UiHelpers.Text(summary, 40, !summaryLive ? "MutedBrush" : summaryLow ? "LowBrush" : "TextBrush");
        value.FontWeight = FontWeights.SemiBold;
        battery.Children.Add(value);
        battery.Children.Add(UiHelpers.Text(UiHelpers.BatteryCaption(d, _monitor.Settings), 13, summaryLive && summaryLow ? "LowBrush" : "MutedBrush"));
        var batteryFrame = new Border { Child = battery, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12) };
        batteryFrame.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        DetailsContent.Children.Add(batteryFrame);

        var connectionCard = DetailSection("Connection");
        connectionCard.Children.Add(InfoRow(UiHelpers.T("Transport"),Localizer.Translate(d.Transport)));
        var readingStatus = !summaryLive && d.Connection == LinkState.Disconnected && UiHelpers.HasRecentAirPodsBroadcast(d)
            ? UiHelpers.T("Nearby broadcast; connection to this PC is not confirmed")
            : summaryLive ? UiHelpers.T("Current reading") : UiHelpers.T("Not live / no valid reading");
        connectionCard.Children.Add(InfoRow(UiHelpers.T("Reading"),readingStatus));
        if (d.BatteryComponents is not null)
            connectionCard.Children.Add(InfoRow(UiHelpers.T("Accuracy"),UiHelpers.T("AirPods broadcast, approx. 10% steps")));
        else if (d.BatteryIsEstimated)
            connectionCard.Children.Add(InfoRow(UiHelpers.T("Accuracy"),UiHelpers.T("System estimate, not an exact percentage")));
        var summaryTime = d.BatteryComponents?.SeenUtc ?? d.LastSeenUtc;
        connectionCard.Children.Add(InfoRow(UiHelpers.T("Last read"),summaryTime?.ToLocalTime().ToString("MM-dd HH:mm:ss")??UiHelpers.T("No reading yet")));
        if (!string.IsNullOrWhiteSpace(d.Message))
        {
            var message = UiHelpers.Text(Localizer.Translate(d.Message), 12, "MutedBrush");
            message.Margin = new Thickness(0, 8, 0, 0);
            connectionCard.Children.Add(message);
        }

        if (d.BatteryComponents is { } components)
        {
            var componentsCard = DetailSection("AirPods battery components");
            bool componentsFresh=UiHelpers.HasRecentAirPodsBroadcast(d);
            if (components.SingleBattery)
                componentsCard.Children.Add(InfoRow(UiHelpers.T("Headphone battery"),UiHelpers.BatteryComponentText(components.Left,components.LeftCharging,componentsFresh)));
            else
            {
                componentsCard.Children.Add(InfoRow(UiHelpers.T("Left earbud"),UiHelpers.BatteryComponentText(components.Left,components.LeftCharging,componentsFresh)));
                componentsCard.Children.Add(InfoRow(UiHelpers.T("Right earbud"),UiHelpers.BatteryComponentText(components.Right,components.RightCharging,componentsFresh)));
                componentsCard.Children.Add(InfoRow(UiHelpers.T("Charging case"),UiHelpers.BatteryComponentText(components.Case,components.CaseCharging,componentsFresh)));
            }
            if(components.SeenUtc is DateTimeOffset seen)
                componentsCard.Children.Add(InfoRow(UiHelpers.T("Last broadcast"),seen.ToLocalTime().ToString("MM-dd HH:mm:ss")));
            var note=UiHelpers.Text(UiHelpers.T("AirPods broadcasts use approximate 10% steps and have no stable device identity. The strongest nearby AirPods signal is used; another pair nearby can be matched by mistake."),12,"MutedBrush");
            note.Margin=new Thickness(0,8,0,0);componentsCard.Children.Add(note);
        }

        var preferences = DetailSection("Device preferences");
        var pin = new CheckBox { Content = UiHelpers.T("Pin to tray icon"), IsChecked = _monitor.Settings.SelectedDeviceId == d.Id };
        pin.Checked += (_, _) => { _monitor.Settings.SelectedDeviceId = d.Id; SaveSettings(); };
        pin.Unchecked += (_, _) => { if (_monitor.Settings.SelectedDeviceId == d.Id) _monitor.Settings.SelectedDeviceId = ""; SaveSettings(); };
        preferences.Children.Add(pin);
        var support = new Expander
        {
            Header = UiHelpers.T("Battery support"), Margin = new Thickness(0, 10, 0, 0),
            IsExpanded = _batterySupportDeviceId == d.Id && _batterySupport?.IsExpanded == true,
            Content = UiHelpers.Text(CompatibilityText(d), 13, "MutedBrush")
        };
        support.PreviewKeyDown += ExpanderPreviewKeyDown;
        preferences.Children.Add(support);
        _batterySupport = support;
        _batterySupportDeviceId = d.Id;

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var refresh = new Button { Content = UiHelpers.T("Read again"), IsEnabled = !_monitor.IsRefreshing, Margin = new Thickness(0, 0, 8, 6) };
        refresh.Click += async (_, _) => await _monitor.RefreshAsync();
        actions.Children.Add(refresh);
        var remove = new Button { Content = UiHelpers.T("Remove device"), Margin = new Thickness(0, 0, 0, 6), ToolTip = UiHelpers.T("Remove from this app. You can restore it in Settings.") };
        AutomationProperties.SetName(remove, UiHelpers.T("Remove {0} from the device list", UiHelpers.DeviceName(d)));
        remove.Click += async (_, _) => await ConfirmRemoveDeviceAsync(d);
        actions.Children.Add(remove);
        preferences.Children.Add(actions);
    }
    private StackPanel DetailSection(string key)
    {
        var section = new StackPanel();
        var heading = UiHelpers.Text(UiHelpers.T(key), 14);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 6);
        section.Children.Add(heading);
        var frame = new Border { Child = section, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 11, 0, 0), Margin = new Thickness(0, 0, 0, 12) };
        frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        DetailsContent.Children.Add(frame);
        return section;
    }
    private async Task ConfirmMergeKeyboardAsync(DeviceSnapshot source, DeviceSnapshot target)
    {
        if (MessageBox.Show(this, UiHelpers.T("Merge {0} into {1}? Only merge if both entries are the same physical keyboard. The connected entry and its current reading will be kept.",
            UiHelpers.DeviceName(source), UiHelpers.DeviceName(target)), UiHelpers.T("Merge duplicate"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await _monitor.MergeKeyboardAsync(source.Id, target.Id); ShowDevice(target.Id); }
        catch (Exception ex) { MessageBox.Show(this, UiHelpers.T("Could not merge keyboard entries: {0}", Localizer.Translate(ex.Message)), "VoltPerch"); }
    }
    private async Task ConfirmRemoveDeviceAsync(DeviceSnapshot device)
    {
        if(MessageBox.Show(this,UiHelpers.T("Remove {0} from this app and clear its saved battery reading?\n\nWindows Bluetooth pairing will stay unchanged. You can restore the device under Removed devices in Settings.",UiHelpers.DeviceName(device)),UiHelpers.T("Remove device"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        try { await _monitor.RemoveDeviceAsync(device.Id);ShowOverview(); }
        catch(Exception ex){MessageBox.Show(this,UiHelpers.T("Could not remove device: {0}",ex.Message),"VoltPerch");}
    }
    public Task RequestRemoveDeviceAsync(string id)
    {
        var device = _monitor.Devices.FirstOrDefault(d => d.Id == id);
        return device == null ? Task.CompletedTask : ConfirmRemoveDeviceAsync(device);
    }
    private static string CompatibilityText(DeviceSnapshot d) => UiHelpers.T("Device: {0}\nTransport: {1}\nSource: {2}\n\n{3}\n\nUnknown battery levels are never shown as 0%. A connected receiver alone does not mean the device is online.",UiHelpers.DeviceName(d),Localizer.Translate(d.Transport),Localizer.Translate(d.Provider),d.IsLive?UiHelpers.T("A valid battery reading is available. The last reading is retained after disconnection and marked as not live."):string.IsNullOrWhiteSpace(d.Message)?UiHelpers.T("No current battery reading is available. Connection status and battery support are shown separately."):Localizer.Translate(d.Message));
    private static Grid InfoRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = UiHelpers.Text(label, 13, "MutedBrush");
        caption.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(caption);
        var content = UiHelpers.Text(value, 13);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }
    private void BuildSettings()
    {
        _buildingSettings=true;SettingsContent.Children.Clear();var s=_monitor.Settings;
        var general=Section("General");
        AddSwitch(general,UiHelpers.T("Launch at sign-in"),s.StartWithWindows,v=>s.StartWithWindows=v,updateAutostart:true);
        AddSwitch(general,UiHelpers.T("Close to tray"),s.CloseToTray,v=>s.CloseToTray=v);
        AddChoice(general,UiHelpers.T("Language"),new[]{("en","English"),("zh-CN","\u7b80\u4f53\u4e2d\u6587")},s.Language,v=>{s.Language=v;Localizer.SetLanguage(v);},()=>{ShowSettings();Render();});
        AddChoice(general,UiHelpers.T("Theme"),new[]{("System",UiHelpers.T("Follow system")),("Light",UiHelpers.T("Light")),("Dark",UiHelpers.T("Dark"))},s.Theme,v=>{s.Theme=v;UiHelpers.Theme(this,s);});
        Note(general,UiHelpers.T("High contrast and reduced motion follow Windows. Fonts are installed locally."));
        var reading=Section("Battery readings");
        AddChoice(reading,UiHelpers.T("Refresh interval"),new[]{("30",UiHelpers.T("30 seconds")),("60",UiHelpers.T("1 minute")),("300",UiHelpers.T("5 minutes"))},s.RefreshSeconds.ToString(),v=>s.RefreshSeconds=int.Parse(v));
        Note(reading,UiHelpers.T("Refreshes when devices connect or disconnect. Only the latest valid battery reading is saved."));
        var tray=Section("System tray");
        _trayDeviceChoice=AddChoice(tray,UiHelpers.T("Display device"),TrayDeviceChoices(_monitor.Devices),s.SelectedDeviceId,v=>s.SelectedDeviceId=v);
        _trayDeviceChoice.DropDownClosed+=(_,_)=>UpdateTrayDeviceChoice(_monitor.Devices);
        Note(tray,UiHelpers.T("Uses exact percentages first, then estimates, then battery levels. Estimates show an approximation mark. Offline readings are excluded."));
        var alerts=Section("Notifications");
        AddSwitch(alerts,UiHelpers.T("Low battery alerts"),s.EnableNotifications,v=>s.EnableNotifications=v);
        AddChoice(alerts,UiHelpers.T("Alert at"),new[]{("10","10%"),("20","20%"),("30","30%")},s.LowBatteryThreshold.ToString(),v=>s.LowBatteryThreshold=int.Parse(v));
        AddSwitch(alerts,UiHelpers.T("Notification sound"),s.NotificationSound,v=>s.NotificationSound=v);
        Note(alerts,UiHelpers.T("Alerts at or below the threshold. Further alerts require a drop of 5 percentage points and a 30-minute gap. Estimates, battery levels and offline readings do not trigger percentage alerts."));
        var data=Section("Devices & data");Note(data,UiHelpers.T("Retains the last battery reading after disconnection and marks it as not live. No history charts or remaining-runtime estimates."));
        Note(data,UiHelpers.T("Data location: {0}",_monitor.DataDirectory));
        var removedList=new StackPanel();
        foreach(var device in _monitor.RemovedDevices)
        {
            var row=new Grid{Margin=new Thickness(0,8,0,4)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var name=UiHelpers.Text(UiHelpers.DeviceName(device.Name,device.Id,device.Kind),14);name.VerticalAlignment=VerticalAlignment.Center;name.Margin=new Thickness(0,0,12,0);row.Children.Add(name);
            var restore=new Button{Content=UiHelpers.T("Restore"),Padding=new Thickness(12,7,12,7)};AutomationProperties.SetName(restore,UiHelpers.T("Restore {0}",UiHelpers.DeviceName(device.Name,device.Id,device.Kind)));Grid.SetColumn(restore,1);row.Children.Add(restore);
            restore.Click+=async(_,_)=>{restore.IsEnabled=false;try{await _monitor.RestoreDeviceAsync(device.Id);BuildSettings();}catch(Exception ex){restore.IsEnabled=true;MessageBox.Show(this,UiHelpers.T("Could not restore device: {0}",ex.Message),"VoltPerch");}};
            removedList.Children.Add(row);
        }
        if(_monitor.RemovedDevices.Count==0)Note(removedList,UiHelpers.T("No removed devices. Remove a device from its details or right-click menu."));
        else Note(removedList,UiHelpers.T("Restored devices get a new battery reading. Previously cleared readings are not recovered."));
        var removed=new Expander{Header=UiHelpers.T("Removed devices ({0})",_monitor.RemovedDevices.Count),Content=removedList,Margin=new Thickness(0,16,0,0)};
        removed.SetResourceReference(ForegroundProperty,"TextBrush");removed.PreviewKeyDown+=ExpanderPreviewKeyDown;data.Children.Add(removed);
        _buildingSettings=false;UpdateSettingsLayout();
    }
    private static void ExpanderPreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter || e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton header || header.TemplatedParent!=sender)return;
        var expander=(Expander)sender;expander.IsExpanded=!expander.IsExpanded;e.Handled=true;
    }
    private StackPanel Section(string title)
    {
        var panel=new StackPanel{Margin=new Thickness(22,18,22,18)};var heading=UiHelpers.Text(UiHelpers.T(title),18);heading.FontWeight=FontWeights.SemiBold;heading.Margin=new Thickness(0,0,0,10);panel.Children.Add(heading);
        var border=new Border{Child=panel,Tag=title,CornerRadius=new CornerRadius(16),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,14,14)};border.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");border.SetResourceReference(Border.BorderBrushProperty,"LineBrush");SettingsContent.Children.Add(border);return panel;
    }
    private void UpdateSettingsLayout()
    {
        double available = SettingsScroll.ViewportWidth > 0 ? SettingsScroll.ViewportWidth : Math.Max(500, Viewport.ActualWidth);
        bool twoColumns=available>=900;
        foreach(Border card in SettingsContent.Children)card.Width=twoColumns&&(string)card.Tag!="Devices & data"?Math.Floor((available-4)/2)-14:Math.Floor(available)-16;
    }
    private static (string Key,string Text)[] TrayDeviceChoices(IReadOnlyList<DeviceSnapshot> devices) =>
        [("",UiHelpers.T("Lowest available battery")), ..devices.Select(d=>(Key:d.Id,Text:UiHelpers.DeviceName(d)))
            .OrderBy(choice=>choice.Text,StringComparer.CurrentCulture).ThenBy(choice=>choice.Key,StringComparer.Ordinal)];

    private void UpdateTrayDeviceChoice(IReadOnlyList<DeviceSnapshot> devices)
    {
        if (_trayDeviceChoice is not { IsDropDownOpen: false } combo || _buildingSettings) return;
        var options = TrayDeviceChoices(devices).Select(o=>new Choice(o.Key,o.Text)).ToArray();
        var choicesChanged = combo.ItemsSource is not Choice[] current || !current.SequenceEqual(options);
        var selected = _monitor.Settings.SelectedDeviceId;
        if (!choicesChanged && Equals(combo.SelectedValue,selected)) return;
        _buildingSettings = true;
        try
        {
            if (choicesChanged) combo.ItemsSource = options;
            combo.SelectedValue = selected;
        }
        finally { _buildingSettings = false; }
    }

    private void AddSwitch(Panel section,string label,bool value,Action<bool> change,bool updateAutostart=false)
    {
        var control=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,7,0,7)};AutomationProperties.SetName(control,label);
        control.Checked+=(_,_)=>{if(_buildingSettings)return;change(true);SaveSettings(updateAutostart);};control.Unchecked+=(_,_)=>{if(_buildingSettings)return;change(false);SaveSettings(updateAutostart);};section.Children.Add(control);
    }
    private ComboBox AddChoice(Panel section,string label,(string Key,string Text)[] options,string current,Action<string> change,Action? afterSave=null)
    {
        var row=new Grid{Margin=new Thickness(0,7,0,7)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(280)});var title=UiHelpers.Text(label);title.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(title);
        var combo=new ComboBox{ItemsSource=options.Select(o=>new Choice(o.Key,o.Text)).ToArray(),DisplayMemberPath="Text",SelectedValuePath="Key",SelectedValue=current};AutomationProperties.SetName(combo,label);combo.SelectionChanged+=(_,_)=>{if(_buildingSettings||combo.SelectedValue is not string key)return;change(key);SaveSettings();afterSave?.Invoke();};Grid.SetColumn(combo,1);row.Children.Add(combo);section.Children.Add(row);
        return combo;
    }
    private void Note(Panel section,string text){var note=UiHelpers.Text(text,12,"MutedBrush");note.Margin=new Thickness(0,9,0,0);section.Children.Add(note);}
    private void SaveSettings(bool updateAutostart=false){_monitor.SaveSettings(updateAutostart);UiHelpers.Theme(this,_monitor.Settings);}
    private async void RefreshClick(object sender,RoutedEventArgs e)=>await _monitor.RefreshAsync();
    private void ThemeClick(object sender,RoutedEventArgs e){_monitor.Settings.Theme=UiHelpers.IsDark(_monitor.Settings)?"Light":"Dark";SaveSettings();Render();if(_settingsPage)BuildSettings();}
    private void SettingsClick(object sender,RoutedEventArgs e)=>ShowSettings();
    private void BackClick(object sender,RoutedEventArgs e){_settingsPage=false;ShowDevicesPage();Render();}
    private void CloseDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not { } previous) return;
        _selectedId = null;
        Render();
        if (!FocusDeviceRow(previous)) FocusFilter();
    }
    private void FilterChecked(object sender, RoutedEventArgs e)
    {
        if (((RadioButton)sender).Tag is not string filter || _filter == filter) return;
        _filter = filter;
        DeviceScroller.ScrollToTop();
        Render();
    }
    private sealed record Choice(string Key,string Text) { public override string ToString() => Text; }
}
