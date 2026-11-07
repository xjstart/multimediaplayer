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
public sealed partial class MediaTile : Border
{
    public MediaItem Item { get; }
    public VlcSession? Session { get; private set; }
    public string? Error { get; private set; }
    public event Action<MediaTile>? Selected;
    public event Action<MediaTile>? AudioStatusChanged;
    public event Action? MetadataChanged;
    public event Action<MediaTile>? RemoveRequested;
    public event Action<MediaTile>? RelocateRequested;
    public event Action<Guid, Guid, TilePlacement>? RepositionRequested;
    public event Action? LayoutDragEnded;
    public event Action<MediaTile>? ViewportChanged;
    public event KeyEventHandler? VideoKeyDown;
    public event Action<string[]>? FilesDropped;
    private readonly Grid _surface = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Canvas _imageLayer = new() { IsHitTestVisible = false };
    private readonly Border _dropPreview = new()
    {
        Background = Brush("#4272C7F0"), BorderBrush = Brush("#72C7F0"),
        BorderThickness = new(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _title;
    private readonly TextBlock _badge;
    private VideoView? _video;
    private VideoViewport? _viewport;
    private Grid? _videoInput;
    private Image? _image;
    private Point _dragStart;
    private Point _lastPanPoint;
    private ViewportGeometry _geometry;
    private int _wheelRemainder;
    private bool _released;
    public bool IsSelected { get; private set; }
    public bool CanZoom => !_released && Error == null && (_image != null || _viewport != null);
    public double EffectiveZoom => _geometry.Zoom > 0 ? _geometry.Zoom : Item.Zoom;

    internal MediaTile(MediaItem item, Func<IEnumerable<MediaItem>> moveTargets)
    {
        Item = item;
        Focusable = true;
        BorderBrush = Brush("#2A3445"); BorderThickness = new(1); Background = Brush("#111722");
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = new(26) }); panel.RowDefinitions.Add(new());
        var header = new Grid { Background = Brush("#1B2330"), Cursor = Cursors.Hand, ToolTip = "拖到画面边缘分区 · 拖到中央交换 · 右键选择目标" };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new(28) });
        _title = new TextBlock { Text = Path.GetFileName(item.Path), Background = Brushes.Transparent, Foreground = Brush("#CBD5E5"), FontSize = 11, Margin = new(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Path };
        _badge = new TextBlock { Text = item.Kind == MediaKind.Image ? "图片" : "视频", Foreground = Brush("#8D9BB1"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 5, 0) };
        var close = new Button { Content = "×", Padding = new(0), Margin = new(2), BorderThickness = new(0), Background = Brushes.Transparent, ToolTip = "移除此画面" };
        close.Click += (_, e) => { e.Handled = true; RemoveRequested?.Invoke(this); };
        Grid.SetColumn(_badge, 1); Grid.SetColumn(close, 2);
        header.Children.Add(_title); header.Children.Add(_badge); header.Children.Add(close);
        header.ContextMenu = CreateMoveMenu(moveTargets);
        header.PreviewMouseLeftButtonDown += (_, _) => { if (!_title.IsMouseOver) SelectFromInput(this); };
        _title.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(header); _title.CaptureMouse(); };
        _title.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_title.IsMouseCaptured) return;
            _title.ReleaseMouseCapture();
            SelectFromInput(this);
        };
        _title.MouseMove += (_, e) =>
        {
            if (!_title.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(header) - _dragStart).Length <= 8) return;
            _title.ReleaseMouseCapture();
            try { DragDrop.DoDragDrop(_title, new DataObject("MmpTile", Item.Id.ToString()), DragDropEffects.Move); }
            finally { LayoutDragEnded?.Invoke(); }
        };
        AllowDrop = true;
        Drop += HandleDrop;
        DragOver += HandleDragOver;
        DragLeave += (_, _) => ClearDropPreview();
        WireSurfaceInput(_surface);
        Panel.SetZIndex(_dropPreview, 1);
        Grid.SetRow(_surface, 1); panel.Children.Add(header); panel.Children.Add(_surface); Child = panel;
        _surface.SizeChanged += (_, _) => Dispatcher.BeginInvoke(ResizeViewport, System.Windows.Threading.DispatcherPriority.Loaded);
        if (item.Kind == MediaKind.Image) SetupImageBrowser();
    }
    public async Task LoadAsync(LibVLC engine, bool paused, bool audioOutputAvailable = true)
    {
        try
        {
            if (Item.Kind == MediaKind.Image)
            {
                bool loaded = await Browser!.InitializeAsync();
                if (_released) return;
                _imageLoadFinished = true;
                if (!loaded) ShowError(Browser.Errors.GetValueOrDefault(Item.Path) ?? "找不到文件，请重新定位。");
                else MetadataChanged?.Invoke();
                UpdateImageNavigation();
            }
            else
            {
                if (!File.Exists(Item.Path)) throw new FileNotFoundException("找不到文件，请重新定位。");
                Session = new(engine, Item);
                Session.SetAudioOutputAvailable(audioOutputAvailable);
                var input = _videoInput = new Grid { Background = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0)), AllowDrop = true, Focusable = true };
                WireSurfaceInput(input);
                input.PreviewKeyDown += (_, e) => VideoKeyDown?.Invoke(input, e);
                input.Drop += HandleDrop;
                input.DragOver += HandleDragOver;
                input.DragLeave += (_, _) => ClearDropPreview();
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
                    if (_released) return;
                    if (Session?.Error is string error) ShowError(error);
                    AudioStatusChanged?.Invoke(this);
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
        _badge.Text = selected ? (Item.Kind == MediaKind.Video ? "● 声音目标" : "● 已选中") : Item.Kind == MediaKind.Image ? "图片" : "视频";
        _badge.Foreground = Brush(selected ? "#81D6FF" : "#8D9BB1");
        UpdateImageNavigation();
    }
    private ContextMenu CreateMoveMenu(Func<IEnumerable<MediaItem>> moveTargets)
    {
        var menu = new ContextMenu();
        var move = new MenuItem { Header = "移到另一画面" };
        menu.Items.Add(move);
        menu.Opened += (_, _) =>
        {
            move.Items.Clear();
            foreach (var (item, index) in moveTargets().Select((item, index) => (item, index)))
            {
                if (item.Id == Item.Id) continue;
                var target = new MenuItem { Header = $"{index + 1}. {Path.GetFileName(item.Path)}", ToolTip = item.Path };
                foreach (var (name, placement) in new[] { ("上方", TilePlacement.Above), ("下方", TilePlacement.Below), ("左侧", TilePlacement.Left), ("右侧", TilePlacement.Right) })
                {
                    var direction = new MenuItem { Header = name };
                    direction.Click += (_, _) => RepositionRequested?.Invoke(Item.Id, item.Id, placement);
                    target.Items.Add(direction);
                }
                move.Items.Add(target);
            }
            move.IsEnabled = move.Items.Count > 0;
        };
        return menu;
    }
    private void HandleDrop(object sender, DragEventArgs e)
    {
        ClearDropPreview();
        if (e.Data.GetData("MmpTile") is string value && Guid.TryParse(value, out var source))
        {
            var placement = source == Item.Id ? null : GetDropPlacement((FrameworkElement)sender, e);
            e.Effects = placement.HasValue ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            if (placement is { } direction) RepositionRequested?.Invoke(source, Item.Id, direction);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            FilesDropped?.Invoke(files);
        }
    }
    private void HandleDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("MmpTile") is string value && Guid.TryParse(value, out var source))
        {
            var placement = source == Item.Id ? null : GetDropPlacement((FrameworkElement)sender, e);
            e.Effects = placement.HasValue ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            if (placement is { } direction) ShowDropPreview(direction);
            else ClearDropPreview();
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }
    private TilePlacement? GetDropPlacement(FrameworkElement input, DragEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var point = e.GetPosition(input);
        // The video's input grid belongs to a separate WPF window.
        if (input != this) point = PointFromScreen(input.PointToScreen(point));
        var x = point.X / ActualWidth;
        var y = point.Y / ActualHeight;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? LayoutEngine.DropPlacement(x, y) : null;
    }
    private void ShowDropPreview(TilePlacement placement)
    {
        var surface = Error == null ? _videoInput ?? _surface : _surface;
        if (_dropPreview.Parent != surface)
        {
            if (_dropPreview.Parent is Panel previous) previous.Children.Remove(_dropPreview);
            surface.Children.Add(_dropPreview);
        }
        _dropPreview.Width = placement is TilePlacement.Left or TilePlacement.Right ? surface.ActualWidth / 2 : double.NaN;
        _dropPreview.Height = placement is TilePlacement.Above or TilePlacement.Below ? surface.ActualHeight / 2 : double.NaN;
        _dropPreview.HorizontalAlignment = placement switch { TilePlacement.Left => HorizontalAlignment.Left, TilePlacement.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch };
        _dropPreview.VerticalAlignment = placement switch { TilePlacement.Above => VerticalAlignment.Top, TilePlacement.Below => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch };
        _dropPreview.Visibility = Visibility.Visible;
    }
    public void ClearDropPreview() => _dropPreview.Visibility = Visibility.Collapsed;

    private void SelectFromInput(FrameworkElement input)
    {
        if (!IsSelected) Selected?.Invoke(this);
        input.Focus();
    }
    private void WireSurfaceInput(Grid input)
    {
        input.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractiveInput(e.OriginalSource as DependencyObject)) return;
            SelectFromInput(input == _videoInput ? input : this);
            if (!CanZoom || Item.Zoom <= 1 || (_geometry.OverflowX <= 0 && _geometry.OverflowY <= 0)) return;
            if (input.CaptureMouse())
            {
                _lastPanPoint = PanPoint(input, e);
                e.Handled = true;
            }
        };
        input.MouseMove += (_, e) =>
        {
            if (!input.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var point = PanPoint(input, e);
            var delta = point - _lastPanPoint;
            _lastPanPoint = point;
            var (x, y) = _geometry.Pan(Item.PanX, Item.PanY, delta.X, delta.Y);
            if (x == Item.PanX && y == Item.PanY) return;
            (Item.PanX, Item.PanY) = (x, y);
            ResizeViewport();
        };
        input.MouseLeftButtonUp += (_, _) => { if (input.IsMouseCaptured) input.ReleaseMouseCapture(); };
        input.PreviewMouseWheel += (_, e) =>
        {
            if (!CanZoom || Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0) return;
            var delta = _wheelRemainder + e.Delta;
            var steps = delta / Mouse.MouseWheelDeltaForOneLine;
            _wheelRemainder = delta % Mouse.MouseWheelDeltaForOneLine;
            if (steps != 0) ChangeZoom(.25 * steps);
            e.Handled = true;
        };
    }
    private Point PanPoint(Grid input, MouseEventArgs e)
    {
        var point = e.GetPosition(input);
        return input == _videoInput ? input.PointToScreen(point) : point;
    }
    public void ChangeZoom(double delta)
    {
        if (!CanZoom) return;
        Item.Zoom = ViewportGeometry.ClampZoom(Item.Zoom + delta);
        if (Item.Zoom == 1) Item.PanX = Item.PanY = 0;
        ResizeViewport();
    }
    public void ResetZoom()
    {
        if (!CanZoom) return;
        Item.Zoom = 1;
        _wheelRemainder = 0;
        ApplyFit();
    }
    public void ApplyFit()
    {
        Item.PanX = Item.PanY = 0;
        ResizeViewport();
    }
    private void ResizeViewport()
    {
        if (!CanZoom) return;
        if (_image != null && _surface.ActualWidth > 0 && _surface.ActualHeight > 0)
        {
            _geometry = ViewportGeometry.Calculate(_surface.ActualWidth, _surface.ActualHeight, Item);
            var content = _geometry.Content;
            Canvas.SetLeft(_image, content.X);
            Canvas.SetTop(_image, content.Y);
            _image.Width = content.Width;
            _image.Height = content.Height;
        }
        else if (_viewport?.Resize(Item) is { } geometry) _geometry = geometry;
        ViewportChanged?.Invoke(this);
    }
    private void ShowError(string message)
    {
        Error = message;
        ClearDropPreview();
        ViewportChanged?.Invoke(this);
        if (_video != null) _video.Visibility = Visibility.Collapsed;
        _surface.Children.Clear();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 280, Margin = new(10) };
        stack.Children.Add(new TextBlock { Text = "无法显示", FontSize = 18, Foreground = Brush("#F2B593"), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A6B3C8"), Margin = new(0, 12, 0, 12), TextAlignment = TextAlignment.Center });
        var button = new Button { Content = "重新定位文件…" }; button.Click += (_, _) => RelocateRequested?.Invoke(this);
        stack.Children.Add(button); _surface.Children.Add(stack);
        AttachImageNavigation();
    }
    public async Task ReleaseAsync()
    {
        _released = true;
        Browser?.Dispose();
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        if (_videoInput?.IsMouseCaptured == true) _videoInput.ReleaseMouseCapture();
        if (Session != null) await Task.Run(async () => await Session.DisposeAsync());
        _viewport?.Dispose(); _viewport = null;
        if (_video != null) { _video.MediaPlayer = null; _video.Dispose(); _video = null; }
        _surface.Children.Clear(); _imageLayer.Children.Clear(); _image = null; _videoInput = null;
        Session = null;
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
