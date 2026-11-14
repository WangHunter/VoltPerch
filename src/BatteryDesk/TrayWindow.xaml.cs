using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BatteryDesk;

public partial class TrayWindow : Window
{
    private readonly DeviceMonitor _monitor;
    private readonly Action<string?> _openMain;
    private Rect? _anchor;
    private bool _showAll;
    private ContextMenu? _deviceMenu;
    public event EventHandler? SettingsRequested;
    public event Action<string>? RemoveRequested;
    public TrayWindow(DeviceMonitor monitor,Action<string?> openMain)
    {
        _monitor=monitor;_openMain=openMain;InitializeComponent();UiHelpers.Theme(this,monitor.Settings);
        monitor.Updated+=Updated;
        Loaded+=(_,_)=>{Render();PositionAtTray();};
        Closed+=(_,_)=>monitor.Updated-=Updated;
        Deactivated+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{if(IsVisible&&!IsActive&&_deviceMenu==null)Close();}),DispatcherPriority.Background);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&_deviceMenu?.IsOpen!=true){Close();e.Handled=true;}};
    }
    public void ShowAtTray(Rect? anchor = null){_anchor=anchor;Render();Show();Activate();}
    private void Updated(object? sender,EventArgs e){if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(Render));}
    private void Render()
    {
        if(_deviceMenu!=null)return;
        UiHelpers.Theme(this,_monitor.Settings);Rows.Children.Clear();RefreshButton.IsEnabled=!_monitor.IsRefreshing;
        var connected=_monitor.Devices.Count(d=>d.Connection==LinkState.Connected);
        var devices=_monitor.Devices.Where(d=>_showAll||d.Connection==LinkState.Connected).ToArray();
        CountLabel.Text=_showAll?UiHelpers.T("All devices · {0}",devices.Length):UiHelpers.T("Connected · {0}",devices.Length);
        FilterButton.Content=_showAll?UiHelpers.T("Connected only ({0})",connected):UiHelpers.T("Show all ({0})",_monitor.Devices.Count);
        Title=_showAll?UiHelpers.T("VoltPerch · All devices"):UiHelpers.T("VoltPerch · Connected devices");
        foreach(var d in devices)Rows.Children.Add(Row(d));
        if(devices.Length==0)
        {
            var message=_monitor.IsRefreshing?UiHelpers.T("Finding devices..."):_showAll||_monitor.Devices.Count==0?UiHelpers.T("No devices yet. Connect one, then refresh."):UiHelpers.T("No connected devices\nChoose Show all to see offline devices and unconfirmed connections.");
            var empty=UiHelpers.Text(message,13,"MutedBrush");empty.Margin=new Thickness(8,16,8,16);empty.TextAlignment=TextAlignment.Center;Rows.Children.Add(empty);
        }
        if(IsVisible)PositionAtTray();
    }
    private UIElement Row(DeviceSnapshot d)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(46)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(76)});
        grid.Children.Add(UiHelpers.Icon(d.Kind,36,d.Name));
        var info=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)};var name=UiHelpers.Text(UiHelpers.DeviceName(d),13);name.FontWeight=FontWeights.SemiBold;name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;info.Children.Add(name);
        var state=new Grid{Margin=new Thickness(0,4,0,0)};state.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(10)});state.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});var dot=new Ellipse{Width=5,Height=5,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};dot.SetResourceReference(Shape.FillProperty,d.Connection==LinkState.Connected?"GoodBrush":"MutedBrush");state.Children.Add(dot);
        var connection=UiHelpers.Text(UiHelpers.ConnectionText(d)+" · "+Localizer.Translate(ShortTransport(d.Transport)),10,"MutedBrush");connection.TextWrapping=TextWrapping.NoWrap;connection.TextTrimming=TextTrimming.CharacterEllipsis;Grid.SetColumn(connection,1);state.Children.Add(connection);info.Children.Add(state);Grid.SetColumn(info,1);grid.Children.Add(info);
        var battery=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,MaxWidth=76};var value=UiHelpers.Text(UiHelpers.BatteryText(d),22,!d.IsLive?"MutedBrush":UiHelpers.Low(d,_monitor.Settings)?"LowBrush":"TextBrush");value.HorizontalAlignment=HorizontalAlignment.Right;battery.Children.Add(value);var caption=UiHelpers.Text(UiHelpers.BatteryCaption(d,_monitor.Settings),10,UiHelpers.Low(d,_monitor.Settings)?"LowBrush":"MutedBrush");caption.TextAlignment=TextAlignment.Right;caption.Margin=new Thickness(0,1,0,0);battery.Children.Add(caption);Grid.SetColumn(battery,2);grid.Children.Add(battery);
        var button=new Button{Content=grid,Style=(Style)FindResource("RowButton"),Padding=new Thickness(4,10,4,10),Tag=d.Id};AutomationProperties.SetName(button,UiHelpers.T("{0}, {1}, {2}, {3}, {4}",UiHelpers.DeviceName(d),UiHelpers.ConnectionText(d),Localizer.Translate(d.Transport),UiHelpers.BatteryText(d),UiHelpers.BatteryCaption(d,_monitor.Settings)));button.Click+=(_,_)=>{_openMain(d.Id);if(IsVisible)Close();};
        var menu=new ContextMenu{Style=(Style)FindResource("DeviceContextMenu")};var remove=new MenuItem{Header=UiHelpers.T("Remove device"),Style=(Style)FindResource("DeviceContextMenuItem")};AutomationProperties.SetName(remove,UiHelpers.T("Remove {0} from the device list",UiHelpers.DeviceName(d)));
        remove.Click+=(_,_)=>{menu.IsOpen=false;if(IsVisible)Close();RemoveRequested?.Invoke(d.Id);};menu.Items.Add(remove);
        button.ContextMenuOpening+=(_,_)=>_deviceMenu=menu;
        menu.Opened+=(_,_)=>{_deviceMenu=menu;foreach(var key in new[]{"TextBrush","HoverBrush","AccentBrush"})menu.Resources[key]=UiHelpers.Brush(this,key);menu.Background=UiHelpers.Brush(this,"SurfaceBrush");menu.Foreground=UiHelpers.Brush(this,"TextBrush");menu.BorderBrush=UiHelpers.Brush(this,"LineBrush");};
        menu.Closed+=(_,_)=>
        {
            if(_deviceMenu==menu)_deviceMenu=null;
            if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(()=>{if(!IsVisible)return;if(IsActive)Render();else Close();}),DispatcherPriority.Background);
        };
        button.ContextMenu=menu;
        var border=new Border{Child=button,BorderThickness=new Thickness(0,0,0,1)};border.SetResourceReference(Border.BorderBrushProperty,"LineBrush");return border;
    }
    private void PositionAtTray()
    {
        var point=_anchor is Rect anchor ? new System.Drawing.Point((int)(anchor.Left+anchor.Width/2),(int)(anchor.Top+anchor.Height/2)) : System.Windows.Forms.Cursor.Position;
        var work=System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        var source=PresentationSource.FromVisual(this);var transform=source?.CompositionTarget?.TransformFromDevice??Matrix.Identity;
        var topLeft=transform.Transform(new Point(work.Left,work.Top));var bottomRight=transform.Transform(new Point(work.Right,work.Bottom));var cursor=transform.Transform(new Point(point.X,point.Y));
        MaxHeight=Math.Max(1,Math.Min(520,bottomRight.Y-topLeft.Y-12));DeviceScroller.MaxHeight=Math.Max(1,Math.Min(300,MaxHeight-170));UpdateLayout();
        Left=Math.Max(topLeft.X+4,Math.Min(cursor.X-ActualWidth+34,bottomRight.X-ActualWidth-4));
        Top=cursor.Y>(topLeft.Y+bottomRight.Y)/2?Math.Max(topLeft.Y+4,bottomRight.Y-ActualHeight-4):topLeft.Y+4;
    }
    private async void RefreshClick(object sender,RoutedEventArgs e)=>await _monitor.RefreshAsync();
    private static string ShortTransport(string transport)=>transport.Contains("2.4",StringComparison.OrdinalIgnoreCase)?"2.4G":transport.Contains("USB",StringComparison.OrdinalIgnoreCase)?"USB":transport;
    private void FilterClick(object sender,RoutedEventArgs e){_showAll=!_showAll;DeviceScroller.ScrollToTop();Render();}
    private void OpenClick(object sender,RoutedEventArgs e){_openMain(null);Close();}
    private void SettingsClick(object sender,RoutedEventArgs e){SettingsRequested?.Invoke(this,EventArgs.Empty);Close();}
}
