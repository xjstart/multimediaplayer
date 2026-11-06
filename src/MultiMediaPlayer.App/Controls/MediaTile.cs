using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Controls;
public sealed class MediaTile : Border
{
    public MediaItem Item { get; }
    public VlcSession? Session { get; private set; }
    public string? Error { get; private set; }
    public event Action<MediaTile>? Selected;
    public event Action? MetadataChanged;
    public event Action<MediaTile>? RemoveRequested;
    public event Action<MediaTile>? RelocateRequested;
    public event Action<Guid, Guid>? SwapRequested;
    public event Action<string[]>? FilesDropped;
    private readonly Grid _surface = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly TextBlock _title;
    private readonly TextBlock _badge;
    private VideoView? _video;
    private VideoViewport? _viewport;
    private Image? _image;
    private Point _dragStart;
    private bool _released;
    public bool IsSelected { get; private set; }
    public MediaTile(MediaItem item)
    {
        Item = item;
        BorderBrush = Brush("#2A3445"); BorderThickness = new(1); Background = Brush("#111722");
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = new(26) }); panel.RowDefinitions.Add(new());
        var header = new Grid { Background = Brush("#1B2330"), Cursor = Cursors.Hand, ToolTip = "拖动标题到另一画面交换位置" };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new(28) });
        _title = new TextBlock { Text = Path.GetFileName(item.Path), Background = Brushes.Transparent, Foreground = Brush("#CBD5E5"), FontSize = 11, Margin = new(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Path };
        _badge = new TextBlock { Text = item.Kind == MediaKind.Image ? "图片" : "视频", Foreground = Brush("#8D9BB1"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 5, 0) };
        var close = new Button { Content = "×", Padding = new(0), Margin = new(2), BorderThickness = new(0), Background = Brushes.Transparent, ToolTip = "移除此画面" };
        close.Click += (_, e) => { e.Handled = true; RemoveRequested?.Invoke(this); };
        Grid.SetColumn(_badge, 1); Grid.SetColumn(close, 2);
        header.Children.Add(_title); header.Children.Add(_badge); header.Children.Add(close);
        header.PreviewMouseLeftButtonDown += (_, _) => Selected?.Invoke(this);
        _title.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(header); _title.CaptureMouse(); };
        _title.PreviewMouseLeftButtonUp += (_, _) => _title.ReleaseMouseCapture();
        _title.MouseMove += (_, e) =>
        {
            if (!_title.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(header) - _dragStart).Length <= 8) return;
            _title.ReleaseMouseCapture();
            DragDrop.DoDragDrop(_title, new DataObject("MmpTile", Item.Id.ToString()), DragDropEffects.Move);
        };
        AllowDrop = true;
        Drop += HandleDrop;
        DragOver += HandleDragOver;
        _surface.MouseLeftButtonDown += (_, _) => Selected?.Invoke(this);
        Grid.SetRow(_surface, 1); panel.Children.Add(header); panel.Children.Add(_surface); Child = panel;
        _surface.SizeChanged += (_, _) => Dispatcher.BeginInvoke(ResizeViewport, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    public async Task LoadAsync(LibVLC engine, bool paused)
    {
        try
        {
            if (!File.Exists(Item.Path)) throw new FileNotFoundException("找不到文件，请重新定位。");
            if (Item.Kind == MediaKind.Image)
            {
                var loaded = await Task.Run(() => ImageLoader.Load(Item.Path));
                if (_released) return;
                Item.DisplayAspect = loaded.Aspect;
                _image = new Image { Source = loaded.Bitmap, Stretch = Item.Fit == FitMode.Cover ? Stretch.UniformToFill : Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                _surface.Children.Add(_image); MetadataChanged?.Invoke();
            }
            else
            {
                Session = new(engine, Item);
                var input = new Grid { Background = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0)), AllowDrop = true };
                input.MouseLeftButtonDown += (_, _) => Selected?.Invoke(this);
                input.Drop += HandleDrop;
                input.DragOver += HandleDragOver;
                _video = new VideoView { MediaPlayer = Session.Player, Content = input };
                _surface.Children.Add(_video);
                Session.AspectChanged += _ => Dispatcher.BeginInvoke(() =>
                {
                    if (_released) return;
                    ResizeViewport();
                    MetadataChanged?.Invoke();
                });
                Session.Changed += () => Dispatcher.BeginInvoke(() =>
                {
                    if (!_released && Session?.Error is string error) ShowError(error);
                });
                // Give the VideoView a chance to attach its native output handle.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                if (Session.Player.Hwnd == 0) throw new InvalidOperationException("无法创建视频显示区域。");
                _viewport = new VideoViewport(Session.Player.Hwnd);
                Session.Player.Hwnd = _viewport.Handle;
                ResizeViewport();
                await Session.StartAsync(Item.PositionMs, paused);
            }
        }
        catch (Exception ex) { Log.Write(ex.ToString()); ShowError(ex.Message); }
    }
    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        BorderBrush = Brush(selected ? "#72C7F0" : "#2A3445");
        _badge.Text = selected ? (Item.Kind == MediaKind.Video ? "● 当前声音" : "● 已选中") : Item.Kind == MediaKind.Image ? "图片" : "视频";
        _badge.Foreground = Brush(selected ? "#81D6FF" : "#8D9BB1");
    }
    private void HandleDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MmpTile") is string s && Guid.TryParse(s, out var id))
        { e.Handled = true; SwapRequested?.Invoke(id, Item.Id); }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        { e.Handled = true; FilesDropped?.Invoke(files); }
    }
    private static void HandleDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("MmpTile")) { e.Effects = DragDropEffects.Move; e.Handled = true; }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }
    public void ApplyFit()
    {
        if (_image != null) _image.Stretch = Item.Fit == FitMode.Cover ? Stretch.UniformToFill : Stretch.Uniform;
        ResizeViewport();
    }
    private void ResizeViewport() => _viewport?.Resize(Item.DisplayAspect, Item.Fit == FitMode.Cover);
    private void ShowError(string message)
    {
        Error = message;
        if (_video != null) _video.Visibility = Visibility.Collapsed;
        _surface.Children.Clear();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 280, Margin = new(10) };
        stack.Children.Add(new TextBlock { Text = "无法显示", FontSize = 18, Foreground = Brush("#F2B593"), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A6B3C8"), Margin = new(0, 12, 0, 12), TextAlignment = TextAlignment.Center });
        var button = new Button { Content = "重新定位文件…" }; button.Click += (_, _) => RelocateRequested?.Invoke(this);
        stack.Children.Add(button); _surface.Children.Add(stack);
    }
    public async Task ReleaseAsync()
    {
        _released = true;
        if (Session != null) await Task.Run(async () => await Session.DisposeAsync());
        _viewport?.Dispose(); _viewport = null;
        if (_video != null) { _video.MediaPlayer = null; _video.Dispose(); _video = null; }
        _surface.Children.Clear(); _image = null;
        Session = null;
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
