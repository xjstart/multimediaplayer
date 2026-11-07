using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MultiMediaPlayer.App.Controls;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App;
public partial class MainWindow : Window
{
    private readonly LibVLC _engine;
    private readonly DefaultAudioDeviceMonitor _audioDevices;
    private CancellationTokenSource? _audioRecoveryCancellation;
    private Task _audioRecoveryTask = Task.CompletedTask;
    private bool _audioOutputAvailable = true, _recoveringAudio;
    private readonly List<MediaTile> _tiles = [];
    private readonly LayoutEngine _layoutEngine = new();
    private readonly List<Thumb> _splitters = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private LayoutNode? _root;
    private LayoutMode _mode;
    private MediaTile? _selected;
    private CancellationTokenSource? _layoutCancellation;
    private bool _updatingControls, _seeking, _closing, _closed, _fullscreen;
    private WindowState _previousWindowState;
    private string? _workspacePath;
    private Guid? _focusItem;
    private long _imageContentRevision;
    private bool _managingImages;
    private static readonly float[] Rates = [.25f, .5f, 1f, 1.5f, 2f, 4f];
    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        LibVLCSharp.Shared.Core.Initialize();
        _engine = VlcEngine.Create();
        _audioDevices = new();
        _audioOutputAvailable = _audioDevices.Current.Available;
        _audioDevices.Changed += state => Dispatcher.BeginInvoke(() =>
        {
            if (!_closing) _audioRecoveryTask = RecoverAudioDeviceAsync(state);
        });
        _timer.Tick += (_, _) => UpdateTime(); _timer.Start();
        Activated += async (_, _) =>
        {
            if (!_closing && _mutation.CurrentCount > 0)
                await Task.WhenAll(_tiles.Select(t => t.Browser?.RefreshAsync() ?? Task.CompletedTask));
        };
        Loaded += async (_, _) =>
        {
            UpdateModeText(); UpdateControls();
            if (App.Arguments.Contains("--verify-image-navigation")) { await Diagnostics.VerifyImageNavigationAsync(this, App.Arguments); return; }
            if (App.Arguments.Contains("--verify-workspace")) { await Diagnostics.VerifyWorkspaceAsync(this, App.Arguments); return; }
            if (App.Arguments.Contains("--verify-images")) { await Diagnostics.VerifyImagesAsync(this, App.Arguments); return; }
            if (App.Arguments.Contains("--soak") || App.Arguments.Contains("--smoke")) { await Diagnostics.RunAsync(this, App.Arguments); return; }
            var paths = App.Arguments.Where(File.Exists).ToArray();
            if (paths.Length == 1 && paths[0].EndsWith(".mmp.json", StringComparison.OrdinalIgnoreCase)) await OpenWorkspaceAsync(paths[0]);
            else if (paths.Length > 0) await ImportAsync(paths);
        };
    }
    internal IReadOnlyList<MediaTile> Tiles => _tiles;
    internal LayoutNode? CurrentLayout => _root;
    internal LayoutMode CurrentMode => _mode;
    internal void DiagnosticMode(LayoutMode mode) => SetMode(mode);
    internal void DiagnosticMute(bool mute) { MuteCheck.IsChecked = mute; UpdateAudio(); }
    internal void DiagnosticVolume(int volume) { VolumeSlider.Value = volume; UpdateAudio(); }
    internal void DiagnosticRatio(double ratio) { if (_root != null && !_root.IsLeaf) { _root.Ratio = ratio; SetCustomMode(); QueueLayout(); } }
    internal async Task ImportAsync(IEnumerable<string> paths, bool paused = false)
    {
        await _mutation.WaitAsync();
        try
        {
            var files = paths.ToArray();
            var valid = files.Where(p => File.Exists(p) && MediaFiles.Supported(p)).ToArray();
            var rejected = files.Except(valid).ToArray();
            if (valid.Length == 0) { Status("未添加媒体。请选择支持的本地视频或图片。"); return; }
            if (_tiles.Count + valid.Length > 9)
            { Status($"最多显示 9 个画面；本次 {valid.Length} 个文件未添加，请减少选择。"); return; }
            var added = new List<MediaTile>();
            foreach (var path in valid)
            {
                var item = new MediaItem { Path = Path.GetFullPath(path), Kind = MediaFiles.Kind(path) };
                if (_mode == LayoutMode.Custom && _root != null && _selected != null)
                {
                    var rect = LayoutEngine.Evaluate(_root, _tiles.Select(t => t.Item).ToArray(), Stage.ActualWidth, Stage.ActualHeight).Tiles[_selected.Item.Id];
                    _root.Insert(_selected.Item.Id, item.Id, rect.Width >= rect.Height ? SplitAxis.Horizontal : SplitAxis.Vertical);
                }
                var tile = CreateTile(item);
                added.Add(tile); Select(tile);
            }
            PlaceNewTiles();
            foreach (var tile in added) await tile.LoadAsync(_engine, paused, _audioOutputAvailable);
            UpdateAudio(); UpdateControls();
            QueueLayout();
            Status(rejected.Length > 0 ? $"已添加 {valid.Length} 个；不支持或不存在：{string.Join("、", rejected.Select(Path.GetFileName))}" : $"已添加 {valid.Length} 个媒体 · 点击视频切换声音");
        }
        finally { _mutation.Release(); }
    }
    private MediaTile CreateTile(MediaItem item)
    {
        var tile = new MediaTile(item, () => _tiles.Select(t => t.Item));
        tile.Selected += Select;
        tile.MetadataChanged += QueueLayout;
        tile.ImageContentChanged += _ =>
        {
            _imageContentRevision++;
            // Preserve an outstanding import/resize/layout action, recalculating from current aspects.
            if (_layoutCancellation != null) QueueLayout();
            UpdateControls();
        };
        tile.ManageImagesRequested += async t => await ManageImagesAsync(t);
        tile.Notice += Status;
        tile.RemoveRequested += async t => await RemoveAsync(t);
        tile.RelocateRequested += async t => await RelocateAsync(t);
        tile.RepositionRequested += Reposition;
        tile.LayoutDragEnded += () => { foreach (var t in _tiles) t.ClearDropPreview(); };
        tile.VideoKeyDown += Window_KeyDown;
        tile.ViewportChanged += t => { if (t == _selected) UpdateZoomControls(); };
        tile.AudioStatusChanged += t => { if (t == _selected) UpdateAudioStatus(); };
        tile.FilesDropped += async files => await ImportAsync(files);
        _tiles.Add(tile); Stage.Children.Add(tile);
        EmptyState.Visibility = Visibility.Collapsed; CountText.Text = $"{_tiles.Count} / 9 画面";
        return tile;
    }
    private void PlaceNewTiles()
    {
        var items = _tiles.Select(t => t.Item).ToArray();
        var layout = _layoutEngine.Arrange(items, Math.Max(1, Stage.ActualWidth), Math.Max(1, Stage.ActualHeight),
            _mode == LayoutMode.Automatic ? LayoutMode.Grid : _mode, _root, _focusItem);
        _root = layout.Root;
        ApplyLayout(layout, true);
    }
    internal void Select(MediaTile tile)
    {
        if (_closing || !_tiles.Contains(tile)) return;
        // Two passes guarantee old audio is muted before new audio is enabled.
        foreach (var t in _tiles) t.Session?.SetAudio(true, (int)VolumeSlider.Value);
        _selected = tile;
        foreach (var t in _tiles) t.SetSelected(t == tile);
        UpdateAudio(); UpdateControls();
    }
    private void UpdateAudio()
    {
        foreach (var t in _tiles.Where(t => t != _selected)) t.Session?.SetAudio(true, (int)VolumeSlider.Value);
        foreach (var t in _tiles) t.Session?.SetAudioOutputAvailable(_audioOutputAvailable);
        _selected?.Session?.SetAudio(MuteCheck.IsChecked == true || !_audioOutputAvailable || _recoveringAudio, (int)VolumeSlider.Value);
        UpdateAudioStatus();
    }
    private void UpdateAudioStatus()
    {
        if (_closing) return;
        if (!_audioOutputAvailable) Status(_audioDevices.Current.Error ?? "等待音频输出设备 · 视频可继续播放");
        else if (_selected?.Session?.GetAudioSnapshot().Error is { } error) Status(error);
    }
    internal async Task RecoverAudioDeviceAsync(AudioDeviceState state)
    {
        if (_closing) return;
        _audioRecoveryCancellation?.Cancel();
        var cancellation = _audioRecoveryCancellation = new();
        var token = cancellation.Token;
        _audioOutputAvailable = state.Available;
        _recoveringAudio = true; UpdateAudio();
        try
        {
            if (!state.Available) return;
            await _mutation.WaitAsync(token);
            try
            {
                if (_closing) return;
                await Task.WhenAll(_tiles.Select(t => t.Session).OfType<VlcSession>()
                    .Select(s => s.RebindDefaultAudioDeviceAsync(token)));
            }
            finally { _mutation.Release(); }
            if (!token.IsCancellationRequested) Status("音频输出设备已恢复 · 声音跟随当前选中画面");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex.ToString()); Status("音频设备恢复失败：" + ex.Message); }
        finally
        {
            if (ReferenceEquals(_audioRecoveryCancellation, cancellation))
            { _audioRecoveryCancellation = null; _recoveringAudio = false; if (!_closing) UpdateAudio(); }
            cancellation.Dispose();
        }
    }
    private void UpdateControls()
    {
        _updatingControls = true;
        var item = _selected?.Item; bool video = _selected?.Session != null;
        SelectedText.Text = item == null ? "尚未选择媒体" : $"{(item.Kind == MediaKind.Image ? "图片" : "视频")}  /  {Path.GetFileName(item.Path)}";
        PlayButton.IsEnabled = SeekSlider.IsEnabled = RateBox.IsEnabled = LoopCheck.IsEnabled = video;
        FitBox.IsEnabled = RemoveButton.IsEnabled = RelocateButton.IsEnabled = item != null;
        HardwareCheck.IsEnabled = item?.Kind == MediaKind.Video;
        HardwareCheck.IsChecked = item?.HardwareDecoding ?? true;
        LoopCheck.IsChecked = item?.Loop ?? true;
        RateBox.SelectedIndex = Array.IndexOf(Rates, item?.Rate ?? 1);
        FitBox.SelectedIndex = item?.Fit == FitMode.Cover ? 1 : 0;
        RatioButton.IsEnabled = _root != null && item != null && _root.ParentOf(item.Id) != null;
        _updatingControls = false;
        UpdateZoomControls();
        UpdateTime();
    }
    private void UpdateZoomControls()
    {
        var tile = _selected;
        var available = tile?.CanZoom == true;
        var zoom = tile?.Item.Zoom ?? 1;
        ZoomText.Text = available ? $"{tile!.EffectiveZoom * 100:0}%" : "—";
        ZoomOutButton.IsEnabled = available && zoom > ViewportGeometry.MinimumZoom;
        ZoomInButton.IsEnabled = available && zoom < ViewportGeometry.MaximumZoom;
        ZoomResetButton.IsEnabled = available && (zoom != 1 || tile!.Item.PanX != 0 || tile.Item.PanY != 0);
    }
    private void UpdateTime()
    {
        var s = _selected?.Session;
        PlayButton.Content = s?.IsPlaying == true ? "Ⅱ 暂停" : "▶ 播放";
        TimeText.Text = $"{Time(s?.PositionMs ?? 0)} / {Time(s?.DurationMs ?? 0)}";
        if (!_seeking) SeekSlider.Value = s?.DurationMs > 0 ? 1000d * s.PositionMs / s.DurationMs : 0;
    }
    private static string Time(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
    private async void QueueLayout()
    {
        if (_closing) return;
        _layoutCancellation?.Cancel();
        var tokenSource = _layoutCancellation = new(); var token = tokenSource.Token;
        try
        {
            await Task.Delay(100, token);
            var items = _tiles.Select(t => t.Item.Copy()).ToArray();
            long contentRevision = _imageContentRevision;
            var previous = _root?.Copy(); var mode = _mode; var focus = _focusItem;
            var width = Stage.ActualWidth; var height = Stage.ActualHeight;
            if (width < 1 || height < 1) return;
            var layout = await Task.Run(() => _layoutEngine.Arrange(items, width, height, mode, previous, focus, token), token);
            if (token.IsCancellationRequested || _closing) return;
            if (contentRevision != _imageContentRevision) { QueueLayout(); return; }
            _root = layout.Root; ApplyLayout(layout, true); UpdateControls();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex.ToString()); Status("布局失败：" + ex.Message); }
        finally { tokenSource.Dispose(); if (ReferenceEquals(_layoutCancellation, tokenSource)) _layoutCancellation = null; }
    }
    private void ApplyLayout(LayoutResult result, bool rebuildSplitters)
    {
        foreach (var tile in _tiles)
        {
            if (!result.Tiles.TryGetValue(tile.Item.Id, out var r)) continue;
            var gutterX = Math.Min(3, r.Width / 4); var gutterY = Math.Min(3, r.Height / 4);
            Canvas.SetLeft(tile, r.X + gutterX); Canvas.SetTop(tile, r.Y + gutterY);
            tile.Width = r.Width - 2 * gutterX; tile.Height = r.Height - 2 * gutterY;
        }
        if (rebuildSplitters)
        {
            foreach (var thumb in _splitters) Stage.Children.Remove(thumb);
            _splitters.Clear();
            foreach (var divider in result.Dividers)
            {
                var thumb = new Thumb { Background = Brushes.Transparent, Cursor = divider.Axis == SplitAxis.Horizontal ? Cursors.SizeWE : Cursors.SizeNS,
                    ToolTip = "拖动调整 · 双击输入比例", Tag = divider.NodeId };
                // Thumb's default control chrome is too opaque for the narrow gutter.
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
                thumb.DragStarted += (_, _) => { _layoutCancellation?.Cancel(); SetCustomMode(); };
                thumb.DragDelta += (_, e) =>
                {
                    if (_root == null) return;
                    var current = LayoutEngine.Evaluate(_root, _tiles.Select(t => t.Item).ToArray(), Stage.ActualWidth, Stage.ActualHeight);
                    var d = current.Dividers.First(x => x.NodeId == (Guid)thumb.Tag);
                    var n = _root.Walk().First(x => x.Id == d.NodeId);
                    n.Ratio = Math.Clamp(n.Ratio + (d.Axis == SplitAxis.Horizontal ? e.HorizontalChange / d.Bounds.Width : e.VerticalChange / d.Bounds.Height), .05, .95);
                    ApplyLayout(LayoutEngine.Evaluate(_root, _tiles.Select(t => t.Item).ToArray(), Stage.ActualWidth, Stage.ActualHeight), false);
                };
                thumb.MouseDoubleClick += (_, _) => EditRatio((Guid)thumb.Tag);
                Stage.Children.Add(thumb); _splitters.Add(thumb); Panel.SetZIndex(thumb, 10);
            }
        }
        foreach (var thumb in _splitters)
        {
            var d = result.Dividers.FirstOrDefault(x => x.NodeId == (Guid)thumb.Tag);
            if (d == null) continue;
            bool horizontal = d.Axis == SplitAxis.Horizontal;
            Canvas.SetLeft(thumb, horizontal ? d.Bounds.X + d.Bounds.Width * d.Ratio - 3 : d.Bounds.X);
            Canvas.SetTop(thumb, horizontal ? d.Bounds.Y : d.Bounds.Y + d.Bounds.Height * d.Ratio - 3);
            thumb.Width = horizontal ? 6 : d.Bounds.Width;
            thumb.Height = horizontal ? d.Bounds.Height : 6;
        }
    }
    private void SetMode(LayoutMode mode)
    {
        _mode = mode; if (mode == LayoutMode.Focus) _focusItem = _selected?.Item.Id;
        UpdateModeText(); QueueLayout();
    }
    private void SetCustomMode() { _mode = LayoutMode.Custom; UpdateModeText(); }
    private void UpdateModeText()
    {
        ModeText.Text = _mode switch { LayoutMode.Automatic => "完整显示 · 智能排列", LayoutMode.Grid => "均匀网格", LayoutMode.Focus => "主画面 60% · 可拖动调整", _ => "自定义布局 · 已锁定分区" };
        foreach (var (button, mode) in new[] { (AutoButton, LayoutMode.Automatic), (GridButton, LayoutMode.Grid), (FocusButton, LayoutMode.Focus) })
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_mode == mode ? "#294E69" : "#252E3D"));
    }
    private void EditRatio(Guid nodeId)
    {
        var n = _root?.Walk().FirstOrDefault(x => x.Id == nodeId && !x.IsLeaf); if (n == null) return;
        var dialog = new RatioDialog(n.Ratio, n.Axis == SplitAxis.Horizontal ? "左侧 : 右侧（此分区）" : "上方 : 下方（此分区）") { Owner = this };
        _layoutCancellation?.Cancel();
        if (dialog.ShowDialog() == true && _root?.Walk().FirstOrDefault(x => x.Id == nodeId) is { } current)
        { _layoutCancellation?.Cancel(); current.Ratio = dialog.Ratio; SetCustomMode(); QueueLayout(); }
    }
    internal void Reposition(Guid source, Guid target, TilePlacement placement)
    {
        if (_closing || _mutation.CurrentCount == 0 || !_tiles.Any(t => t.Item.Id == source) || !_tiles.Any(t => t.Item.Id == target) ||
            _root?.Reposition(source, target, placement) is not { } next) return;

        _layoutCancellation?.Cancel();
        _root = next;
        SetCustomMode();
        ApplyLayout(LayoutEngine.Evaluate(next, _tiles.Select(t => t.Item).ToArray(), Stage.ActualWidth, Stage.ActualHeight), true);
        UpdateControls();
    }
    internal async Task RemoveAsync(MediaTile tile)
    {
        await _mutation.WaitAsync();
        try
        {
            if (!_tiles.Contains(tile)) return;
            _layoutCancellation?.Cancel();
            _tiles.Remove(tile); _root = LayoutNode.Remove(_root, tile.Item.Id);
            if (_selected == tile) _selected = null;
            await tile.ReleaseAsync(); Stage.Children.Remove(tile);
            if (_selected == null && _tiles.Count > 0) Select(_tiles[0]);
            EmptyState.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{_tiles.Count} / 9 画面"; UpdateControls(); QueueLayout();
        }
        finally { _mutation.Release(); }
    }
    private async Task RelocateAsync(MediaTile tile)
    {
        var dialog = MediaDialog(false); if (dialog.ShowDialog(this) != true) return;
        if (!MediaFiles.Supported(dialog.FileName)) { Status("不支持此文件类型。"); return; }
        if (!_tiles.Contains(tile) || _closing) return;
        if (tile.Browser != null && MediaFiles.Kind(dialog.FileName) == MediaKind.Image)
        {
            if (await tile.Browser.RelocateAsync(dialog.FileName)) await tile.Browser.RefreshAsync();
            return;
        }
        var relocated = tile.Item.Copy();
        relocated.ImageCollectionPaths = null;
        relocated.Path = dialog.FileName; relocated.Kind = MediaFiles.Kind(dialog.FileName); relocated.PositionMs = 0;
        relocated.Zoom = 1; relocated.PanX = relocated.PanY = 0;
        await ReplaceTileAsync(tile, true, relocated);
    }
    private async Task ManageImagesAsync(MediaTile tile)
    {
        if (_managingImages || _closing || tile.Browser == null || _mutation.CurrentCount == 0) return;
        _managingImages = true;
        try
        {
            Select(tile);
            await tile.Browser.RefreshAsync();
            await tile.Browser.WhenIdleAsync();
            if (!_closing && _tiles.Contains(tile)) new ImageCollectionDialog(tile.Item, tile.Browser) { Owner = this }.ShowDialog();
        }
        finally { _managingImages = false; }
    }
    internal async Task ReplaceTileAsync(MediaTile tile, bool paused, MediaItem? item = null)
    {
        await _mutation.WaitAsync();
        try
        {
            var index = _tiles.IndexOf(tile); var wasSelected = _selected == tile;
            if (index < 0) return;
            _tiles.Remove(tile); await tile.ReleaseAsync(); Stage.Children.Remove(tile);
            var replacement = CreateTile(item ?? tile.Item); _tiles.Remove(replacement); _tiles.Insert(index, replacement);
            await replacement.LoadAsync(_engine, paused, _audioOutputAvailable);
            if (wasSelected) Select(replacement); else UpdateAudio();
            QueueLayout();
        }
        finally { _mutation.Release(); }
    }
    internal Workspace Snapshot()
    {
        var items = _tiles.Select(t => { var i = t.Item.Copy(); if (t.Session?.Ready == true) i.PositionMs = t.Session.PositionMs; return i; }).ToList();
        var layout = _root;
        if (layout == null || !layout.Walk().Where(n => n.IsLeaf).Select(n => n.ItemId!.Value).Order().SequenceEqual(items.Select(i => i.Id).Order()))
            layout = _layoutEngine.Arrange(items, Math.Max(1, Stage.ActualWidth), Math.Max(1, Stage.ActualHeight), LayoutMode.Grid).Root;
        return new Workspace { Items = items, Layout = layout?.Copy(), Mode = _mode,
            SelectedId = _selected?.Item.Id, FocusItemId = _focusItem, Volume = (int)VolumeSlider.Value, Muted = MuteCheck.IsChecked == true };
    }
    internal async Task OpenWorkspaceAsync(string path)
    {
        Workspace workspace;
        try { workspace = await WorkspaceStore.LoadAsync(path); }
        catch (Exception ex) { Status("无法打开工作区：" + ex.Message); return; }
        await _mutation.WaitAsync();
        try
        {
            _layoutCancellation?.Cancel();
            foreach (var tile in _tiles.ToArray()) { await tile.ReleaseAsync(); Stage.Children.Remove(tile); }
            _tiles.Clear(); _selected = null;
            _root = workspace.Layout; _mode = workspace.Mode; _focusItem = workspace.FocusItemId;
            VolumeSlider.Value = workspace.Volume; MuteCheck.IsChecked = workspace.Muted;
            foreach (var item in workspace.Items) CreateTile(item);
            PlaceNewTiles();
            foreach (var tile in _tiles) await tile.LoadAsync(_engine, true, _audioOutputAvailable);
            var selected = _tiles.FirstOrDefault(t => t.Item.Id == workspace.SelectedId) ?? _tiles.FirstOrDefault();
            if (selected != null) Select(selected);
            _workspacePath = path; EmptyState.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = $"{_tiles.Count} / 9 画面"; UpdateModeText(); UpdateControls(); QueueLayout();
            Status("工作区已打开 · 所有视频保持暂停");
        }
        finally { _mutation.Release(); }
    }
    internal async Task SaveWorkspaceAsync(string path)
    {
        await _mutation.WaitAsync();
        try { await WorkspaceStore.SaveAsync(path, Snapshot()); _workspacePath = path; Status("工作区已保存：" + Path.GetFileName(path)); }
        catch (Exception ex) { Status("保存失败：" + ex.Message); }
        finally { _mutation.Release(); }
    }
    private static OpenFileDialog MediaDialog(bool multiple) => new()
    {
        Title = "选择视频或图片", Multiselect = multiple,
        Filter = "媒体文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.wmv;*.mpg;*.mpeg;*.ts;*.m2ts;*.flv;*.ogv;*.3gp;*.jpg;*.jpeg;*.png;*.webp;*.bmp|所有文件|*.*"
    };
    private async void Add_Click(object sender, RoutedEventArgs e) { var d = MediaDialog(true); if (d.ShowDialog(this) == true) await ImportAsync(d.FileNames); }
    private async void Open_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "播放器工作区|*.mmp.json" }; if (d.ShowDialog(this) == true) await OpenWorkspaceAsync(d.FileName); }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "播放器工作区|*.mmp.json", FileName = _workspacePath == null ? "我的工作区.mmp.json" : Path.GetFileName(_workspacePath), DefaultExt = ".mmp.json", AddExtension = true };
        if (d.ShowDialog(this) == true) await SaveWorkspaceAsync(d.FileName);
    }
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; await ImportAsync(files); } }
    private void Auto_Click(object sender, RoutedEventArgs e) => SetMode(LayoutMode.Automatic);
    private void Grid_Click(object sender, RoutedEventArgs e) => SetMode(LayoutMode.Grid);
    private void Focus_Click(object sender, RoutedEventArgs e) => SetMode(LayoutMode.Focus);
    private void Ratio_Click(object sender, RoutedEventArgs e) { if (_selected != null && _root?.ParentOf(_selected.Item.Id) is LayoutNode n) EditRatio(n.Id); }
    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => QueueLayout();
    private async void Play_Click(object sender, RoutedEventArgs e) { if (_selected?.Session is { } s) { if (s.IsPlaying) await s.PauseAsync(); else await s.PlayAsync(); } }
    private async void PlayAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _tiles.ToArray()) if (t.Session != null) await t.Session.PlayAsync(); }
    private async void PauseAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _tiles.ToArray()) if (t.Session != null) await t.Session.PauseAsync(); }
    private async void RestartAll_Click(object sender, RoutedEventArgs e) { foreach (var t in _tiles.ToArray()) if (t.Session != null) await t.Session.RestartAsync(); }
    private void Seek_Begin(object sender, MouseButtonEventArgs e) => _seeking = true;
    private async void Seek_End(object sender, MouseButtonEventArgs e) { await SeekSelectedAsync(); _seeking = false; }
    private async void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await SeekSelectedAsync();
    }
    private Task SeekSelectedAsync() => _selected?.Session is { } s ? s.SeekAsync((long)(SeekSlider.Value / 1000 * s.DurationMs)) : Task.CompletedTask;
    private void Rate_Changed(object sender, SelectionChangedEventArgs e) { if (!_updatingControls && _selected?.Session is { } s && RateBox.SelectedIndex >= 0) s.SetRate(Rates[RateBox.SelectedIndex]); }
    private void Fit_Changed(object sender, SelectionChangedEventArgs e) { if (!_updatingControls && _selected != null) { _selected.Item.Fit = FitBox.SelectedIndex == 1 ? FitMode.Cover : FitMode.Contain; _selected.ApplyFit(); QueueLayout(); } }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _selected?.ChangeZoom(-.25);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _selected?.ChangeZoom(.25);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _selected?.ResetZoom();
    private void Loop_Click(object sender, RoutedEventArgs e) { if (_selected != null) _selected.Item.Loop = LoopCheck.IsChecked == true; }
    private void Mute_Click(object sender, RoutedEventArgs e) => UpdateAudio();
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (MuteCheck != null) UpdateAudio(); }
    private async void Hardware_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } tile || tile.Item.Kind != MediaKind.Video) return;
        tile.Item.PositionMs = tile.Session?.PositionMs ?? 0; bool paused = tile.Session?.IsPlaying != true;
        tile.Item.HardwareDecoding = HardwareCheck.IsChecked == true;
        await ReplaceTileAsync(tile, paused);
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) { if (_selected != null) await RemoveAsync(_selected); }
    private async void Relocate_Click(object sender, RoutedEventArgs e) { if (_selected != null) await RelocateAsync(_selected); }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    internal void ToggleFullscreen()
    {
        if (!_fullscreen) { _previousWindowState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = _previousWindowState; }
        _fullscreen = !_fullscreen;
        HeaderBar.Visibility = LayoutBar.Visibility = ControlBar.Visibility = StatusBar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Shell.Margin = _fullscreen ? new Thickness(0) : new Thickness(18, 12, 18, 8);
        StageFrame.Margin = _fullscreen ? new Thickness(0) : new Thickness(0, 0, 0, 10);
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _closing) return;
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { Add_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBox or ComboBox or Slider or Button or CheckBox)) { Play_Click(this, e); e.Handled = true; }
        else if ((e.Key is Key.Left or Key.Right) && Keyboard.Modifiers == ModifierKeys.None &&
                 !MediaTile.IsInteractiveInput(Keyboard.FocusedElement as DependencyObject) &&
                 _selected?.Browser is { } browser)
        {
            e.Handled = true;
            _ = browser.NavigateAsync(e.Key == Key.Left ? -1 : 1);
        }
        else if ((e.Key is Key.Left or Key.Right) && (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) &&
                 Keyboard.FocusedElement is not (TextBoxBase or ComboBox or Slider or MenuItem) &&
                 _selected?.Session is { Ready: true, Error: null } session)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            var step = Keyboard.Modifiers == ModifierKeys.Shift ? 30_000L : 5_000L;
            _ = session.SeekRelativeAsync(e.Key == Key.Left ? -step : step);
        }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; _layoutCancellation?.Cancel(); _timer.Stop(); IsEnabled = false;
        _audioDevices.Dispose();
        _audioRecoveryCancellation?.Cancel();
        await _audioRecoveryTask;
        await _mutation.WaitAsync();
        foreach (var tile in _tiles) await tile.ReleaseAsync();
        await Task.Run(() => _engine.Dispose());
        _closed = true; Close();
    }
    internal void Status(string message) => StatusText.Text = message;
}
