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
    private ContextMenu? _deviceMenu;
    private readonly ScaleTransform _contentScale = new(1,1);
    public MainWindow(DeviceMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        ScaleHost.LayoutTransform = _contentScale;
        UiHelpers.Theme(this, _monitor.Settings);
        _monitor.Updated += MonitorUpdated;
        Loaded += (_, _) => { UpdateWindowScale(); Render(); };
        Closed += (_, _) => _monitor.Updated -= MonitorUpdated;
        Viewport.SizeChanged += (_, _) => UpdateWindowScale();
        SettingsScroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) UpdateSettingsLayout(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_settingsPage) BackClick(this, e); else CloseDetailsClick(this, e); e.Handled = true; } };
    }
    private void UpdateWindowScale()
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        // WPF measures this client area in DIPs; Windows DPI scaling is already applied.
        var scale = Math.Clamp(Math.Min(Viewport.ActualWidth / 1104, Viewport.ActualHeight / 721), .70, 1.4);
        if (Math.Abs(_contentScale.ScaleX - scale) > .0001)
        {
            _contentScale.ScaleX = scale;
            _contentScale.ScaleY = scale;
        }
        bool compact = Viewport.ActualWidth / scale < 960;
        if (_compact != compact) { _compact = compact; Render(); }
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
        if (_selectedId != null && !all.Any(d => d.Id == _selectedId)) _selectedId = null;
        CountLabel.Text = UiHelpers.T("{0} devices · {1} connected",all.Count,all.Count(d => d.Connection == LinkState.Connected));
        foreach (var button in new[] { AllFilter, ConnectedFilter, OfflineFilter }) button.SetResourceReference(BackgroundProperty, (string)button.Tag == _filter ? "SurfaceBrush" : "TileBrush");
        DeviceRows.Children.Clear();
        var devices = all.Where(d => _filter == "All" || (_filter == "Connected" ? d.Connection == LinkState.Connected : d.Connection == LinkState.Disconnected)).ToArray();
        EmptyPanel.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = all.Count == 0 ? (_monitor.IsRefreshing ? UiHelpers.T("Finding devices and reading battery levels...") : UiHelpers.T("Connect a device or plug in its receiver, then refresh.")) : UiHelpers.T("No devices match this filter.");
        foreach (var device in devices) DeviceRows.Children.Add(CreateDeviceRow(device));
        UpdateDetailLayout();
        if (_selectedId != null) RenderDetails(all.FirstOrDefault(d => d.Id == _selectedId));
    }
    private UIElement CreateDeviceRow(DeviceSnapshot device)
    {
        bool narrow = _compact || _selectedId != null;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!narrow) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        bool showComponents = UiHelpers.HasSeparateAirPodsComponents(device);
        double batteryWidth = showComponents ? (narrow ? 224 : 236) : (narrow ? 146 : 176);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(batteryWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.Children.Add(UiHelpers.Icon(device.Kind,56,device.Name));
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
        var title = UiHelpers.Text(UiHelpers.DeviceName(device), narrow ? 17 : 18); title.FontWeight = FontWeights.SemiBold; name.Children.Add(title);
        var type = UiHelpers.Text(UiHelpers.KindName(device.Kind) + " · " + Localizer.Translate(device.Transport), 12, "MutedBrush"); type.Margin = new Thickness(0,5,0,0); name.Children.Add(type);
        if (narrow) { var line = ConnectionLine(device); line.Margin = new Thickness(0,7,0,0); name.Children.Add(line); }
        Grid.SetColumn(name,1); grid.Children.Add(name);
        if (!narrow) { var connection = ConnectionLine(device); connection.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(connection,2); grid.Children.Add(connection); }
        UIElement battery;
        if (showComponents)
        {
            battery = UiHelpers.AirPodsComponentPanel(device, batteryWidth, 11, 14, 9, 10, _monitor.Settings);
        }
        else
        {
            var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = batteryWidth - 1 };
            var number = UiHelpers.Text(UiHelpers.BatteryText(device), 29, !device.IsLive ? "MutedBrush" : UiHelpers.Low(device,_monitor.Settings) ? "LowBrush" : "TextBrush"); number.HorizontalAlignment = HorizontalAlignment.Right; summary.Children.Add(number);
            var caption = UiHelpers.Text(UiHelpers.BatteryCaption(device,_monitor.Settings), 12, UiHelpers.Low(device,_monitor.Settings) ? "LowBrush" : "MutedBrush"); caption.TextAlignment = TextAlignment.Right; caption.Margin = new Thickness(0,3,0,0); summary.Children.Add(caption);
            battery = summary;
        }
        Grid.SetColumn(battery,grid.ColumnDefinitions.Count-2); grid.Children.Add(battery);
        var chevron=UiHelpers.Text("›",24,"MutedBrush");chevron.VerticalAlignment=VerticalAlignment.Center;chevron.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(chevron,grid.ColumnDefinitions.Count-1);grid.Children.Add(chevron);
        var button = new Button { Content=grid, Style=(Style)FindResource("RowButton"), Tag=device.Id };
        if (device.Id == _selectedId) button.SetResourceReference(BackgroundProperty,"HoverBrush");
        AutomationProperties.SetName(button, UiHelpers.T("{0}, {1}, {2}, {3}",UiHelpers.DeviceName(device),UiHelpers.ConnectionText(device),Localizer.Translate(device.Transport),UiHelpers.BatteryAccessibilityText(device,_monitor.Settings)));
        button.Click += (_,_) => ShowDevice(device.Id);
        var menu = new ContextMenu { Style = (Style)FindResource("DeviceContextMenu") };
        var removeItem = new MenuItem { Header = UiHelpers.T("Remove device"), Style = (Style)FindResource("DeviceContextMenuItem") };
        AutomationProperties.SetName(removeItem, UiHelpers.T("Remove {0} from the device list",UiHelpers.DeviceName(device)));
        removeItem.Click += async (_,_) => { menu.IsOpen = false; await ConfirmRemoveDeviceAsync(device); };
        menu.Items.Add(removeItem);
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
    private StackPanel ConnectionLine(DeviceSnapshot device)
    {
        var panel=new StackPanel { Orientation=Orientation.Horizontal };
        var dot=new Ellipse { Width=8,Height=8,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center };dot.SetResourceReference(Shape.FillProperty,device.Connection==LinkState.Connected?"GoodBrush":"MutedBrush");panel.Children.Add(dot);
        panel.Children.Add(UiHelpers.Text(UiHelpers.ConnectionText(device),13,device.Connection==LinkState.Connected?"TextBrush":"MutedBrush"));return panel;
    }
    public void ShowDevice(string id)
    {
        _selectedId=id;_settingsPage=false;ShowDevicesPage();Render();
    }
    public void ShowSettings()
    {
        _settingsPage=true;PageTitle.Text=UiHelpers.T("Settings");PageSubtitle.Text=UiHelpers.T("Make it work your way");BackButton.Visibility=Visibility.Visible;DeviceToolbar.Visibility=Visibility.Collapsed;FilterBar.Visibility=Visibility.Collapsed;DevicesLayout.Visibility=Visibility.Collapsed;SettingsScroll.Visibility=Visibility.Visible;BuildSettings();
    }
    public void ShowOverview() { _selectedId = null; _settingsPage = false; ShowDevicesPage(); Render(); }
    private void ShowDevicesPage()
    {
        PageTitle.Text=UiHelpers.T("Your devices");PageSubtitle.Text=UiHelpers.T("Connections and battery, at a glance");BackButton.Visibility=Visibility.Collapsed;DeviceToolbar.Visibility=Visibility.Visible;FilterBar.Visibility=Visibility.Visible;DevicesLayout.Visibility=Visibility.Visible;SettingsScroll.Visibility=Visibility.Collapsed;
    }
    private void UpdateDetailLayout()
    {
        bool show=_selectedId!=null;
        DetailFrame.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        if (show && _compact) { DeviceListFrame.Visibility=Visibility.Collapsed;Grid.SetColumn(DetailFrame,0);DetailColumn.Width=new GridLength(0);DetailFrame.Margin=new Thickness(0); }
        else {DeviceListFrame.Visibility=Visibility.Visible;Grid.SetColumn(DetailFrame,1);DetailColumn.Width=new GridLength(show?330:0);DetailFrame.Margin=new Thickness(18,0,0,0);}
    }
    private void RenderDetails(DeviceSnapshot? d)
    {
        DetailsContent.Children.Clear(); if(d==null)return;
        var icon=UiHelpers.Icon(d.Kind,64,d.Name);icon.HorizontalAlignment=HorizontalAlignment.Left;DetailsContent.Children.Add(icon);
        var name=UiHelpers.Text(UiHelpers.DeviceName(d),20);name.FontWeight=FontWeights.SemiBold;name.Margin=new Thickness(0,16,0,0);DetailsContent.Children.Add(name);
        if (UiHelpers.HasSeparateAirPodsComponents(d))
        {
            var summaryLabel=UiHelpers.Text(UiHelpers.T("Lowest earbud"),13,"MutedBrush");summaryLabel.Margin=new Thickness(0,14,0,0);DetailsContent.Children.Add(summaryLabel);
        }
        var value=UiHelpers.Text(UiHelpers.BatteryText(d),43,!d.IsLive?"MutedBrush":UiHelpers.Low(d,_monitor.Settings)?"LowBrush":"TextBrush");value.Margin=new Thickness(0,UiHelpers.HasSeparateAirPodsComponents(d)?2:14,0,0);DetailsContent.Children.Add(value);
        DetailsContent.Children.Add(UiHelpers.Text(UiHelpers.BatteryCaption(d,_monitor.Settings),14,"MutedBrush"));
        DetailsContent.Children.Add(Divider());
        DetailsContent.Children.Add(InfoRow(UiHelpers.T("Connection"),UiHelpers.ConnectionText(d)));
        DetailsContent.Children.Add(InfoRow(UiHelpers.T("Transport"),d.Transport));
        var readingStatus = !d.IsLive && d.Connection == LinkState.Disconnected && UiHelpers.HasRecentAirPodsBroadcast(d)
            ? UiHelpers.T("Nearby broadcast; connection to this PC is not confirmed")
            : d.IsLive ? UiHelpers.T("Current reading") : UiHelpers.T("Not live / no valid reading");
        DetailsContent.Children.Add(InfoRow(UiHelpers.T("Reading"),readingStatus));
        if (d.BatteryComponents is not null)
            DetailsContent.Children.Add(InfoRow(UiHelpers.T("Accuracy"),UiHelpers.T("AirPods broadcast, approx. 10% steps")));
        else if (d.BatteryIsEstimated)
            DetailsContent.Children.Add(InfoRow(UiHelpers.T("Accuracy"),UiHelpers.T("System estimate, not an exact percentage")));
        DetailsContent.Children.Add(InfoRow(UiHelpers.T("Last read"),d.LastSeenUtc?.ToLocalTime().ToString("MM-dd HH:mm:ss")??UiHelpers.T("No reading yet")));
        if (d.BatteryComponents is { } components)
        {
            DetailsContent.Children.Add(Divider());
            var componentHeading=UiHelpers.Text(UiHelpers.T("AirPods battery components"),16);componentHeading.FontWeight=FontWeights.SemiBold;DetailsContent.Children.Add(componentHeading);
            bool componentsFresh=UiHelpers.HasRecentAirPodsBroadcast(d);
            if (components.SingleBattery)
                DetailsContent.Children.Add(InfoRow(UiHelpers.T("Headphone battery"),UiHelpers.BatteryComponentText(components.Left,components.LeftCharging,componentsFresh)));
            else
            {
                DetailsContent.Children.Add(InfoRow(UiHelpers.T("Left earbud"),UiHelpers.BatteryComponentText(components.Left,components.LeftCharging,componentsFresh)));
                DetailsContent.Children.Add(InfoRow(UiHelpers.T("Right earbud"),UiHelpers.BatteryComponentText(components.Right,components.RightCharging,componentsFresh)));
                DetailsContent.Children.Add(InfoRow(UiHelpers.T("Charging case"),UiHelpers.BatteryComponentText(components.Case,components.CaseCharging,componentsFresh)));
            }
            if(components.SeenUtc is DateTimeOffset seen)
                DetailsContent.Children.Add(InfoRow(UiHelpers.T("Last broadcast"),seen.ToLocalTime().ToString("MM-dd HH:mm:ss")));
            var note=UiHelpers.Text(UiHelpers.T("AirPods broadcasts use approximate 10% steps and have no stable device identity. The strongest nearby AirPods signal is used; another pair nearby can be matched by mistake."),12,"MutedBrush");
            note.Margin=new Thickness(0,8,0,0);DetailsContent.Children.Add(note);
        }
        if(!string.IsNullOrWhiteSpace(d.Message)) {var message=UiHelpers.Text(d.Message,13,"MutedBrush");message.Margin=new Thickness(0,8,0,0);DetailsContent.Children.Add(message);}
        DetailsContent.Children.Add(Divider());
        var heading=UiHelpers.Text(UiHelpers.T("Device preferences"),16);heading.FontWeight=FontWeights.SemiBold;DetailsContent.Children.Add(heading);
        var pin=new CheckBox {Content=UiHelpers.T("Pin to tray icon"),IsChecked=_monitor.Settings.SelectedDeviceId==d.Id,Margin=new Thickness(0,12,0,0)};
        pin.Checked+=(_,_)=>{_monitor.Settings.SelectedDeviceId=d.Id;SaveSettings();};
        pin.Unchecked+=(_,_)=>{if(_monitor.Settings.SelectedDeviceId==d.Id)_monitor.Settings.SelectedDeviceId="";SaveSettings();};DetailsContent.Children.Add(pin);
        var support=new Expander {Header=UiHelpers.T("Battery support"),Margin=new Thickness(0,20,0,0)};
        support.SetResourceReference(ForegroundProperty,"TextBrush");support.PreviewKeyDown+=ExpanderPreviewKeyDown;
        support.Content=UiHelpers.Text(CompatibilityText(d),13,"MutedBrush");DetailsContent.Children.Add(support);
        var refresh=new Button {Content=UiHelpers.T("Read again"),Margin=new Thickness(0,20,0,0)};refresh.Click+=async(_,_)=>await _monitor.RefreshAsync();DetailsContent.Children.Add(refresh);
        var remove=new Button {Content=UiHelpers.T("Remove device"),Margin=new Thickness(0,10,0,0),ToolTip=UiHelpers.T("Remove from this app. You can restore it in Settings.")};
        remove.Foreground=new SolidColorBrush(UiHelpers.IsDark(_monitor.Settings)?Color.FromRgb(255,139,139):Color.FromRgb(181,48,48));
        AutomationProperties.SetName(remove,UiHelpers.T("Remove {0} from the device list",UiHelpers.DeviceName(d)));
        remove.Click+=async(_,_)=>await ConfirmRemoveDeviceAsync(d);
        DetailsContent.Children.Add(remove);
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
    private Border Divider(){var b=new Border{Height=1,Margin=new Thickness(0,20,0,16)};b.SetResourceReference(Border.BackgroundProperty,"LineBrush");return b;}
    private Grid InfoRow(string label,string value)
    {
        var row=new Grid{Margin=new Thickness(0,5,0,5)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(76)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.Children.Add(UiHelpers.Text(label,13,"MutedBrush"));var right=UiHelpers.Text(value,13);right.TextAlignment=TextAlignment.Right;Grid.SetColumn(right,1);row.Children.Add(right);return row;
    }
    private void BuildSettings()
    {
        _buildingSettings=true;SettingsContent.Children.Clear();var s=_monitor.Settings;
        var general=Section("General");
        AddSwitch(general,UiHelpers.T("Launch at sign-in"),s.StartWithWindows,v=>s.StartWithWindows=v);
        AddSwitch(general,UiHelpers.T("Close to tray"),s.CloseToTray,v=>s.CloseToTray=v);
        AddChoice(general,UiHelpers.T("Language"),new[]{("en","English"),("zh-CN","\u7b80\u4f53\u4e2d\u6587")},s.Language,v=>{s.Language=v;Localizer.SetLanguage(v);},()=>{ShowSettings();Render();});
        AddChoice(general,UiHelpers.T("Theme"),new[]{("System",UiHelpers.T("Follow system")),("Light",UiHelpers.T("Light")),("Dark",UiHelpers.T("Dark"))},s.Theme,v=>{s.Theme=v;UiHelpers.Theme(this,s);});
        Note(general,UiHelpers.T("High contrast and reduced motion follow Windows. Fonts are installed locally."));
        var reading=Section("Battery readings");
        AddChoice(reading,UiHelpers.T("Refresh interval"),new[]{("30",UiHelpers.T("30 seconds")),("60",UiHelpers.T("1 minute")),("300",UiHelpers.T("5 minutes"))},s.RefreshSeconds.ToString(),v=>s.RefreshSeconds=int.Parse(v));
        Note(reading,UiHelpers.T("Refreshes when devices connect or disconnect. Only the latest valid battery reading is saved."));
        var tray=Section("System tray");
        var targets=new List<(string,string)>{("",UiHelpers.T("Lowest available battery"))};targets.AddRange(_monitor.Devices.Select(d=>(d.Id,UiHelpers.DeviceName(d))));
        AddChoice(tray,UiHelpers.T("Display device"),targets.ToArray(),s.SelectedDeviceId,v=>s.SelectedDeviceId=v);
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
        double available=SettingsScroll.ViewportWidth > 0 ? SettingsScroll.ViewportWidth : Math.Max(500,Viewport.ActualWidth/_contentScale.ScaleX-76);
        bool twoColumns=available>=900;
        foreach(Border card in SettingsContent.Children)card.Width=twoColumns&&(string)card.Tag!="Devices & data"?Math.Floor((available-4)/2)-14:Math.Floor(available)-16;
    }
    private void AddSwitch(Panel section,string label,bool value,Action<bool> change)
    {
        var control=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,7,0,7)};AutomationProperties.SetName(control,label);
        control.Checked+=(_,_)=>{if(_buildingSettings)return;change(true);SaveSettings();};control.Unchecked+=(_,_)=>{if(_buildingSettings)return;change(false);SaveSettings();};section.Children.Add(control);
    }
    private void AddChoice(Panel section,string label,(string Key,string Text)[] options,string current,Action<string> change,Action? afterSave=null)
    {
        var row=new Grid{Margin=new Thickness(0,7,0,7)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(280)});var title=UiHelpers.Text(label);title.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(title);
        var combo=new ComboBox{ItemsSource=options.Select(o=>new Choice(o.Key,o.Text)).ToArray(),DisplayMemberPath="Text",SelectedValuePath="Key",SelectedValue=current};AutomationProperties.SetName(combo,label);combo.SelectionChanged+=(_,_)=>{if(_buildingSettings||combo.SelectedValue is not string key)return;change(key);SaveSettings();afterSave?.Invoke();};Grid.SetColumn(combo,1);row.Children.Add(combo);section.Children.Add(row);
    }
    private void Note(Panel section,string text){var note=UiHelpers.Text(text,12,"MutedBrush");note.Margin=new Thickness(0,9,0,0);section.Children.Add(note);}
    private void SaveSettings(){_monitor.SaveSettings();UiHelpers.Theme(this,_monitor.Settings);}
    private async void RefreshClick(object sender,RoutedEventArgs e)=>await _monitor.RefreshAsync();
    private void ThemeClick(object sender,RoutedEventArgs e){_monitor.Settings.Theme=UiHelpers.IsDark(_monitor.Settings)?"Light":"Dark";SaveSettings();Render();if(_settingsPage)BuildSettings();}
    private void SettingsClick(object sender,RoutedEventArgs e)=>ShowSettings();
    private void BackClick(object sender,RoutedEventArgs e){_settingsPage=false;ShowDevicesPage();Render();}
    private void CloseDetailsClick(object sender,RoutedEventArgs e){_selectedId=null;Render();}
    private void FilterClick(object sender,RoutedEventArgs e){_filter=(string)((Button)sender).Tag;Render();}
    private sealed record Choice(string Key,string Text) { public override string ToString() => Text; }
}
