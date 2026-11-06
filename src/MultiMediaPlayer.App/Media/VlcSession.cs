using LibVLCSharp.Shared;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Media;

public sealed class VlcSession : IMediaSession
{
    private readonly MediaItem _item;
    private readonly LibVLCSharp.Shared.Media _media;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private volatile bool _disposed;
    private bool _starting, _startPaused;
    private bool _playRequested;
    private long _startPosition;
    private (long Target, long RequestedAt)? _lastRelativeSeek;
    private bool _muted = true;
    private int _volume = 60;
    public MediaPlayer Player { get; }
    public string? Error { get; private set; }
    public bool Ready { get; private set; }
    public event Action? Changed;
    public event Action<double>? AspectChanged;
    public long PositionMs => _disposed ? 0 : Math.Max(0, Player.Time);
    public long DurationMs => _disposed ? 0 : Math.Max(0, Player.Length);
    public bool IsPlaying => !_disposed && Player.IsPlaying;
    public bool IsMuted => _muted;
    public (int Displayed, int Lost) Statistics
    {
        get
        {
            if (_disposed) return (0, 0);
            var s = _media.Statistics;
            return (s.DisplayedPictures, s.LostPictures);
        }
    }

    public VlcSession(LibVLC engine, MediaItem item)
    {
        _item = item;
        _media = new(engine, item.Path, FromType.FromPath);
        _media.AddOption(item.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none");
        Player = new(engine) { Media = _media, EnableHardwareDecoding = item.HardwareDecoding };
        Player.Mute = true;
        Player.Playing += OnPlaying;
        Player.EncounteredError += OnError;
        Player.EndReached += OnEnded;
    }
    public Task StartAsync(long position, bool paused) => Execute(() =>
    {
        _lastRelativeSeek = null;
        _starting = true; Ready = false; _startPosition = position; _startPaused = paused; _playRequested = !paused;
        Player.Mute = true;
        if (!Player.Play()) { Error = "无法打开此视频。"; Changed?.Invoke(); }
    });
    private void OnPlaying(object? sender, EventArgs e)
    {
        // libVLC callbacks must return before any synchronous player calls.
        _ = Task.Run(async () =>
        {
            await Task.Delay(120);
            await ExecuteAsync(async () =>
            {
                if (Error != null) return;
                var tracks = _media.Tracks;
                for (int attempt = 0; attempt < 10 && Player.VoutCount == 0 && !tracks.Any(t => t.TrackType == TrackType.Video); attempt++)
                {
                    await Task.Delay(50);
                    tracks = _media.Tracks;
                }
                if (Player.VoutCount == 0 && !tracks.Any(t => t.TrackType == TrackType.Video))
                {
                    MarkUnreadable();
                    Player.Stop();
                    return;
                }
                foreach (var track in tracks)
                {
                    if (track.TrackType != TrackType.Video) continue;
                    var v = track.Data.Video;
                    if (v.Width == 0 || v.Height == 0) continue;
                    double aspect = (double)v.Width / v.Height * (v.SarDen > 0 && v.SarNum > 0 ? (double)v.SarNum / v.SarDen : 1);
                    // LibVLC orientation values 4–7 swap the two display axes.
                    if ((int)v.Orientation >= 4) aspect = 1 / aspect;
                    if (Math.Abs(_item.DisplayAspect - aspect) > 1e-6)
                    {
                        _item.DisplayAspect = aspect;
                        AspectChanged?.Invoke(aspect);
                    }
                    break;
                }
                if (_starting)
                {
                    _starting = false;
                    if (_item.Rate != 1) Player.SetRate(_item.Rate);
                    if (_startPaused)
                    {
                        Player.SetPause(true);
                        for (int attempt = 0; attempt < 15 && Player.IsPlaying; attempt++) await Task.Delay(30);
                    }
                    if (_startPosition > 0)
                    {
                        for (int attempt = 0; attempt < 15 && !Player.IsSeekable; attempt++) await Task.Delay(30);
                        if (Player.IsSeekable)
                        {
                            var target = Player.Length > 0 ? Math.Min(_startPosition, Math.Max(0, Player.Length - 100)) : _startPosition;
                            Player.Time = target;
                            // The native input thread updates position asynchronously.
                            for (int attempt = 0; attempt < 30 && Math.Abs(Player.Time - target) > 300; attempt++) await Task.Delay(30);
                        }
                    }
                }
                Ready = true; Player.Volume = _volume; Player.Mute = _muted;
                Changed?.Invoke();
            });
        });
    }
    private void OnError(object? sender, EventArgs e)
    {
        MarkUnreadable();
    }
    private void MarkUnreadable()
    {
        Error = "视频无法播放，文件可能损坏、没有视频轨道或编码不受支持。";
        Changed?.Invoke();
    }
    private void OnEnded(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await Execute(() =>
            {
                _lastRelativeSeek = null;
                if (Error != null) return;
                if (_media.Statistics.DisplayedPictures == 0 && !_media.Tracks.Any(t => t.TrackType == TrackType.Video))
                {
                    MarkUnreadable();
                    return;
                }
                if (_item.Loop && _playRequested) { Player.Stop(); Player.Play(); }
                Changed?.Invoke();
            });
        });
    }
    private Task Execute(Action action) => ExecuteAsync(() => { action(); return Task.CompletedTask; });
    private Task ExecuteOffThread(Action action) => ExecuteAsync(() => Task.Run(action));
    private async Task ExecuteAsync(Func<Task> action)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try { if (!_disposed) await action().ConfigureAwait(false); }
        catch (Exception ex) { Error = ex.Message; Log.Write(ex.ToString()); Changed?.Invoke(); }
        finally { _commands.Release(); }
    }
    public Task PlayAsync() => Task.Run(() => Execute(() =>
    {
        _playRequested = true;
        if (_starting) _startPaused = false;
        if (Player.State == VLCState.Ended) Player.Stop();
        Player.Play();
    }));
    public Task PauseAsync() => Task.Run(() => Execute(() =>
    {
        _playRequested = false;
        if (_starting) _startPaused = true;
        else Player.SetPause(true);
    }));
    public Task SeekAsync(long milliseconds) => ExecuteOffThread(() =>
    {
        _lastRelativeSeek = null;
        Seek(milliseconds);
    });
    internal Task SeekRelativeAsync(long delta) => ExecuteOffThread(() =>
    {
        var duration = Player.Length;
        if (!Ready || _starting || Error != null || duration <= 0 || !Player.IsSeekable) return;
        var position = PositionMs;
        var now = Environment.TickCount64;
        if (_lastRelativeSeek is { } previous && now - previous.RequestedAt <= 500 &&
            Math.Abs(position - previous.Target) > 300)
            position = previous.Target;
        var target = PlaybackPosition.Offset(position, delta, duration);
        Seek(target);
        _lastRelativeSeek = (target, now);
    });
    private void Seek(long milliseconds)
    {
        if (_starting) { _startPosition = milliseconds; return; }
        if (Player.State == VLCState.Ended) { _starting = true; _startPaused = true; _startPosition = milliseconds; Player.Stop(); Player.Play(); }
        else if (Player.IsSeekable) Player.Time = PlaybackPosition.Offset(milliseconds, 0, Player.Length);
    }
    public Task RestartAsync() => ExecuteOffThread(() =>
    {
        _lastRelativeSeek = null;
        _playRequested = true;
        if (_starting) { _startPaused = false; _startPosition = 0; return; }
        if (Player.State == VLCState.Ended) Player.Stop();
        else if (Player.IsSeekable) Player.Time = 0;
        Player.Play();
    });
    public void SetAudio(bool muted, int volume)
    {
        _muted = muted; _volume = volume;
        if (_disposed) return;
        // Mute immediately, including during startup, before unmuting another session.
        Player.Mute = !Ready || muted; Player.Volume = volume;
    }
    public void SetRate(float rate) { _item.Rate = rate; if (!_disposed) Player.SetRate(rate); }
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            Player.Playing -= OnPlaying; Player.EncounteredError -= OnError; Player.EndReached -= OnEnded;
            Player.Stop(); Player.Dispose(); _media.Dispose();
        }
        finally { _commands.Release(); }
    }
}
