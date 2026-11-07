using System.IO;
using System.Windows.Media.Imaging;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Media;

internal sealed record ImageFrame(BitmapSource Bitmap, double Aspect);

/// <summary>UI-thread-owned state. One decoder per view; only the newest request can commit.</summary>
internal sealed class ImageBrowser : IDisposable
{
    private sealed record Request(long Revision, List<string> Candidates, List<string> Paths,
        List<string>? Collection, bool SetCollection, bool Reset, CancellationToken Token, TaskCompletionSource<bool> Completion);
    private readonly MediaItem _item;
    private readonly Func<string, Task<ImageFrame>> _decode;
    private readonly CancellationTokenSource _lifetime = new();
    private List<string> _paths;
    private Request? _pending;
    private Task _worker = Task.CompletedTask;
    private Task? _refresh;
    private string? _target;
    private long _revision, _directoryRevision;
    private bool _disposed;
    public IReadOnlyList<string> Paths => _paths;
    public Dictionary<string, string> Errors { get; } = new(ImageSequence.PathComparer);
    public event Action<ImageFrame, bool>? Committed;
    public event Action? Changed;
    public event Action<string>? Notice;
    public Task WhenIdleAsync() => _worker;

    public ImageBrowser(MediaItem item, Func<string, Task<ImageFrame>>? decode = null)
    {
        _item = item;
        _paths = item.ImageCollectionPaths?.ToList() ?? [item.Path];
        _decode = decode ?? (path => Task.Run(() => { var frame = ImageLoader.Load(path); return new ImageFrame(frame.Bitmap, frame.Aspect); }));
    }

    public async Task<bool> InitializeAsync()
    {
        var refresh = RefreshAsync();
        bool loaded = await RequestAsync([_item.Path], _paths.ToList(), null, false, false);
        await refresh;
        return loaded;
    }

    public Task RefreshAsync()
    {
        if (_disposed || _item.ImageCollectionPaths != null) return Task.CompletedTask;
        return _refresh is { IsCompleted: false } ? _refresh : _refresh = RefreshDirectoryAsync();
    }

    private async Task RefreshDirectoryAsync()
    {
        long revision = _directoryRevision;
        string path = _item.Path;
        try
        {
            var result = await ImageDirectory.ReadAsync(Path.GetDirectoryName(path)!, token: _lifetime.Token);
            if (_disposed || revision != _directoryRevision || _item.ImageCollectionPaths != null ||
                !ImageSequence.PathComparer.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(_item.Path))) return;
            // Keep a missing current file as an anchor so navigation still works after restoring a workspace.
            _paths = ImageSequence.Append(result.Paths, [_item.Path]).Order(ImageSequence.NaturalOrder).ToList();
            Changed?.Invoke();
            if (result.Errors.Count > 0) Notice?.Invoke("部分目录无法读取：" + result.Errors[0]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Notice?.Invoke("读取图片目录失败：" + ex.Message); }
    }

    public async Task<bool> NavigateAsync(int direction)
    {
        if (_disposed) return false;
        await RefreshAsyncIfNeeded();
        var candidates = ImageSequence.Navigate(_paths, _target ?? _item.Path, direction).ToList();
        if (candidates.Count == 0) return false;
        _target = candidates[0];
        return await RequestAsync(candidates, _paths.ToList(), null, false, true);
    }

    private Task RefreshAsyncIfNeeded() => _refresh == null ? RefreshAsync() : _refresh;

    public async Task<bool> ApplyAsync(List<string>? collection, CancellationToken token = default)
    {
        if (_disposed || token.IsCancellationRequested) return false;
        List<string> paths;
        if (collection == null)
        {
            var result = await ImageDirectory.ReadAsync(Path.GetDirectoryName(_item.Path)!, token: token);
            if (result.Errors.Count > 0) Notice?.Invoke("部分目录无法读取：" + result.Errors[0]);
            paths = ImageSequence.Append(result.Paths, [_item.Path]).Order(ImageSequence.NaturalOrder).ToList();
        }
        else paths = ImageSequence.Distinct(collection);
        if (paths.Count == 0) return false;
        if (paths.Any(p => !MediaFiles.Images.Contains(Path.GetExtension(p))))
            throw new InvalidDataException("图片集只能包含 JPEG、PNG、WebP、BMP 图片。");
        var candidates = ImageSequence.ApplyCandidates(_paths, paths, _item.Path);
        // A pure list edit can retain the decoded current frame without resetting its viewport.
        return await RequestAsync(candidates, paths, collection == null ? null : paths.ToList(), true, false, token);
    }

    public Task<bool> RelocateAsync(string path, CancellationToken token = default)
    {
        var collection = _item.ImageCollectionPaths == null ? null : ImageSequence.Distinct(
            _item.ImageCollectionPaths.Select(p => ImageSequence.PathComparer.Equals(p, _item.Path) ? path : p));
        return RequestAsync([path], collection ?? [path], collection, true, true, token);
    }

    private Task<bool> RequestAsync(List<string> candidates, List<string> paths, List<string>? collection,
        bool setCollection, bool reset, CancellationToken token = default)
    {
        if (_disposed || token.IsCancellationRequested) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _target = candidates.FirstOrDefault();
        _pending?.Completion.TrySetResult(false);
        _pending = new(++_revision, candidates, paths, collection, setCollection, reset, token, completion);
        if (_worker.IsCompleted) _worker = RunAsync();
        Changed?.Invoke();
        return completion.Task;
    }

    private async Task RunAsync()
    {
        // Force asynchronous startup so _worker is assigned before a synchronous test decoder returns.
        await Task.Yield();
        while (!_disposed && _pending is { } request)
        {
            _pending = null;
            bool success = false; int skipped = 0;
            foreach (string path in request.Candidates)
            {
                if (!Current(request)) break;
                try
                {
                    var frame = await _decode(path);
                    if (!Current(request)) break;
                    bool changedFile = !ImageSequence.PathComparer.Equals(_item.Path, path);
                    if (request.SetCollection)
                    {
                        _item.ImageCollectionPaths = request.Collection?.ToList();
                        _paths = request.Paths.ToList();
                        _directoryRevision++;
                        if (request.Collection == null) _refresh = null;
                    }
                    _item.Path = path; _item.DisplayAspect = frame.Aspect;
                    if (request.Reset || changedFile) { _item.Zoom = 1; _item.PanX = _item.PanY = 0; }
                    Errors.Remove(path);
                    Committed?.Invoke(frame, request.Reset || changedFile);
                    success = true;
                    if (skipped > 0) Notice?.Invoke($"已跳过 {skipped} 张无法读取的图片。");
                    break;
                }
                catch (Exception ex)
                {
                    if (!Current(request)) break;
                    Errors[path] = ex.Message; skipped++;
                    if (_item.ImageCollectionPaths == null && ex is FileNotFoundException or DirectoryNotFoundException)
                        _ = RefreshAsync();
                }
            }
            if (request.Revision == _revision)
            {
                _target = null;
                if (Current(request))
                {
                    if (!success) Notice?.Invoke("没有可显示的图片，已保留原画面。可在“管理图片”中查看失效项。");
                    Changed?.Invoke();
                }
            }
            request.Completion.TrySetResult(success);
        }
    }

    private bool Current(Request request) => !_disposed && request.Revision == _revision && !request.Token.IsCancellationRequested;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _revision++; _lifetime.Cancel(); _lifetime.Dispose();
        _pending?.Completion.TrySetResult(false); _pending = null;
        Committed = null; Changed = null; Notice = null;
    }
}
