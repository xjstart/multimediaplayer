using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Controls;

internal sealed class ImageCollectionDialog : Window
{
    private sealed record Row(string Path, string Name, string Directory, string Status);
    private readonly ImageBrowser _browser;
    private readonly string _current;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Margin = new(3, 0, 3, 10) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon, Margin = new(3, 8, 3, 4) };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, Background = new SolidColorBrush(Color.FromRgb(17, 23, 34)), Foreground = Brushes.White };
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹" };
    private readonly List<Button> _editButtons = [];
    private readonly Button _create, _directory, _apply;
    private List<string> _draft;
    private bool _custom, _busy, _closed;
    private int _rowsRevision;

    public ImageCollectionDialog(MediaItem item, ImageBrowser browser)
    {
        _browser = browser; _current = item.Path; _draft = browser.Paths.ToList(); _custom = item.ImageCollectionPaths != null;
        Title = "管理图片"; Width = 800; Height = 550; MinWidth = 580; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        var root = new DockPanel { Margin = new(18) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _apply = Button("应用", ApplyAsync); _apply.IsDefault = true;
        var cancel = new Button { Content = "取消", IsCancel = true };
        actions.Children.Add(_apply); actions.Children.Add(cancel); footer.Children.Add(actions); root.Children.Add(footer);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); top.Children.Add(_source);
        var modes = new WrapPanel();
        _create = Button("创建自定义图片集", () => { _custom = true; UpdateState(); return Task.CompletedTask; });
        _directory = Button("切回目录浏览", async () =>
        {
            var result = await ImageDirectory.ReadAsync(Path.GetDirectoryName(_current)!, token: _lifetime.Token);
            _draft = ImageSequence.Append(result.Paths, [_current]).Order(ImageSequence.NaturalOrder).ToList();
            _custom = false; _message.Text = "应用后将替换自定义图片集。"; RefreshRows();
        });
        modes.Children.Add(_create); modes.Children.Add(_directory); top.Children.Add(modes);
        var edit = new WrapPanel();
        AddEdit(edit, "添加图片…", AddFilesAsync); AddEdit(edit, "添加文件夹…", AddFolderAsync);
        edit.Children.Add(_recursive);
        AddEdit(edit, "移除所选", () =>
        {
            _draft = ImageSequence.Remove(_draft, SelectedPaths()); RefreshRows(); return Task.CompletedTask;
        });
        AddEdit(edit, "上移", () => MoveAsync(-1)); AddEdit(edit, "下移", () => MoveAsync(1));
        top.Children.Add(edit);
        top.Children.Add(new TextBlock { Text = "多选：Ctrl / Shift · 移除仅修改图片集，不删除文件 · 至少保留一项", Foreground = Brushes.LightSlateGray, Margin = new(3, 6, 3, 10), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(top);
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "文件名", Width = 235, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "所在目录", Width = 325, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Directory)) });
        view.Columns.Add(new GridViewColumn { Header = "状态", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Status)) });
        _list.View = view;
        var rowStyle = new Style(typeof(ListViewItem));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding(nameof(Row.Path))));
        _list.ItemContainerStyle = rowStyle;
        VirtualizingPanel.SetIsVirtualizing(_list, true); VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        root.Children.Add(_list); Content = root;
        Loaded += (_, _) => RefreshRows();
        Closing += OnClosing;
        Closed += (_, _) => _lifetime.Dispose();
    }

    private Button Button(string caption, Func<Task> action)
    {
        var button = new Button { Content = caption, Padding = new(10, 5, 10, 5) };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; UpdateState(); _message.Text = "";
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) _message.Text = ex.Message; }
            finally { _busy = false; if (!_closed) UpdateState(); }
        };
        return button;
    }

    private void AddEdit(Panel panel, string caption, Func<Task> action)
    {
        var button = Button(caption, action); panel.Children.Add(button); _editButtons.Add(button);
    }

    private List<string> SelectedPaths() => _list.SelectedItems.Cast<Row>().Select(r => r.Path).ToList();

    private Task MoveAsync(int direction)
    {
        var selected = SelectedPaths(); _draft = ImageSequence.Move(_draft, selected, direction);
        RefreshRows(selected); return Task.CompletedTask;
    }

    private Task AddFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = "添加图片", Multiselect = true, Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
        if (dialog.ShowDialog(this) == true)
        {
            var supported = dialog.FileNames.Where(p => MediaFiles.Images.Contains(Path.GetExtension(p))).ToArray();
            _draft = ImageSequence.Append(_draft, supported); RefreshRows();
            if (supported.Length != dialog.FileNames.Length) _message.Text = "已跳过不支持的文件，只能添加 JPEG、PNG、WebP、BMP 图片。";
        }
        return Task.CompletedTask;
    }

    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "添加图片文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        var result = await ImageDirectory.ReadAsync(dialog.FolderName, _recursive.IsChecked == true, _lifetime.Token);
        if (_closed) return;
        _draft = ImageSequence.Append(_draft, result.Paths); RefreshRows();
        _message.Text = result.Errors.Count > 0 ? $"已跳过 {result.Errors.Count} 个无法访问的目录：{result.Errors[0]}" : $"扫描到 {result.Paths.Count} 张图片。";
    }

    private async void RefreshRows(List<string>? selection = null)
    {
        int revision = ++_rowsRevision;
        var paths = _draft.ToArray(); var errors = new Dictionary<string, string>(_browser.Errors, ImageSequence.PathComparer);
        var selected = (selection ?? SelectedPaths()).ToHashSet(ImageSequence.PathComparer);
        UpdateState();
        var rows = await Task.Run(() => paths.Select(p => new Row(p, Path.GetFileName(p), Path.GetDirectoryName(p) ?? "",
            string.Join(" · ", new[] { ImageSequence.PathComparer.Equals(p, _current) ? "当前" : "",
                errors.TryGetValue(p, out var error) ? error : File.Exists(p) ? "" : "文件缺失" }.Where(s => s.Length > 0)))).ToArray());
        if (_closed || revision != _rowsRevision) return;
        _list.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.Path))) _list.SelectedItems.Add(row);
    }

    private void UpdateState()
    {
        _source.Text = _custom ? $"自定义图片集 · {_draft.Count} 项 · 点击“应用”后统一生效" :
            $"当前文件夹 · {_draft.Count} 项 · 创建自定义图片集后可以增删和排序";
        _create.Visibility = _custom ? Visibility.Collapsed : Visibility.Visible;
        _directory.Visibility = _custom ? Visibility.Visible : Visibility.Collapsed;
        _create.IsEnabled = _directory.IsEnabled = _apply.IsEnabled = !_busy;
        foreach (var button in _editButtons) button.IsEnabled = _custom && !_busy;
        _recursive.IsEnabled = _custom && !_busy;
        _list.IsEnabled = !_busy;
    }

    private async Task ApplyAsync()
    {
        bool applied = await _browser.ApplyAsync(_custom ? _draft.ToList() : null, _lifetime.Token);
        if (_closed) return;
        if (applied) DialogResult = true;
        else { _message.Text = "没有可显示的图片，未提交修改。请添加可用图片或修复失效项。"; RefreshRows(); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closed = true; _lifetime.Cancel();
    }
}
