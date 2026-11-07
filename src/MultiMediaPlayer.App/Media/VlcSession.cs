using LibVLCSharp.Shared;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Media;

public sealed class VlcSession : IMediaSession
{
    private readonly MediaItem _item;
    private readonly LibVLCSharp.Shared.Media _media;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _audioGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private long _generation, _deviceGeneration;
    private bool _hasAudioTrack, _audioHeld, _fallbackUsed;
    private bool _outputAvailable = true;
    private int _audioRecoveryRestarts;
    internal int AudioRecoveryRestarts { get { lock (_audioGate) return _audioRecoveryRestarts; } }
    private Task? _audioWatch;
    private string? _audioError;
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
    public long PositionMs { get { lock (_audioGate) return _disposed ? 0 : Math.Max(0, Player.Time); } }
    public long DurationMs { get { lock (_audioGate) return _disposed ? 0 : Math.Max(0, Player.Length); } }
    public bool IsPlaying { get { lock (_audioGate) return !_disposed && Player.IsPlaying; } }
    public bool IsMuted { get { lock (_audioGate) return _muted; } }
    public (int Displayed, int Lost) Statistics
    {
        get
        {
            lock (_audioGate)
            {
                if (_disposed) return (0, 0);
                var s = _media.Statistics;
                return (s.DisplayedPictures, s.LostPictures);
            }
        }
    }

    public VlcSession(LibVLC engine, MediaItem item)
    {
        _lifetimeToken = _lifetime.Token;
        _item = item;
        _media = new(engine, item.Path, FromType.FromPath);
        _media.AddOption(item.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none");
        Player = new(engine) { Media = _media, EnableHardwareDecoding = item.HardwareDecoding };
        lock (_audioGate) ApplyAudioLocked();
        Player.Playing += OnPlaying;
        Player.EncounteredError += OnError;
        Player.EndReached += OnEnded;
    }
    public Task StartAsync(long position, bool paused) => ExecuteOffThread(() =>
    {
        _lastRelativeSeek = null;
        BeginInput(position, paused);
    });
    private void BeginInput(long position, bool paused)
    {
        Interlocked.Increment(ref _generation);
        _starting = true; _startPosition = position; _startPaused = paused; _playRequested = !paused;
        lock (_audioGate) { Ready = false; ApplyAudioLocked(); }
        if (!Player.Play()) { Error = "无法打开此视频。"; Changed?.Invoke(); }
    }
    private void OnPlaying(object? sender, EventArgs e)
    {
        var generation = Interlocked.Read(ref _generation);
        // libVLC callbacks must return before any synchronous player calls.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(120, _lifetimeToken); }
            catch (OperationCanceledException) { return; }
            await ExecuteAsync(async () =>
            {
                if (Error != null || generation != Interlocked.Read(ref _generation)) return;
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
                lock (_audioGate)
                {
                    if (_disposed || generation != Interlocked.Read(ref _generation)) return;
                    _hasAudioTrack = tracks.Any(t => t.TrackType == TrackType.Audio);
                    Ready = true;
                    ApplyAudioLocked();
                    EnsureAudioWatchLocked();
                }
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
        var generation = Interlocked.Read(ref _generation);
        _ = Task.Run(async () =>
        {
            await Execute(() =>
            {
                _lastRelativeSeek = null;
                if (Error != null || generation != Interlocked.Read(ref _generation)) return;
                if (_media.Statistics.DisplayedPictures == 0 && !_media.Tracks.Any(t => t.TrackType == TrackType.Video))
                {
                    MarkUnreadable();
                    return;
                }
                if (_item.Loop && _playRequested) { Player.Stop(); BeginInput(0, false); }
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
    public Task PlayAsync() => ExecuteOffThread(() =>
    {
        _playRequested = true;
        if (_starting) _startPaused = false;
        if (Player.State == VLCState.Ended) { Player.Stop(); BeginInput(0, false); }
        else Player.Play();
    });
    public Task PauseAsync() => ExecuteOffThread(() =>
    {
        _playRequested = false;
        if (_starting) _startPaused = true;
        else Player.SetPause(true);
    });
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
        if (Player.State == VLCState.Ended) { Player.Stop(); BeginInput(milliseconds, true); }
        else if (Player.IsSeekable) Player.Time = PlaybackPosition.Offset(milliseconds, 0, Player.Length);
    }
    public Task RestartAsync() => ExecuteOffThread(() =>
    {
        _lastRelativeSeek = null;
        _playRequested = true;
        if (_starting) { _startPaused = false; _startPosition = 0; return; }
        if (Player.State == VLCState.Ended) { Player.Stop(); BeginInput(0, false); return; }
        else if (Player.IsSeekable) Player.Time = 0;
        Player.Play();
    });
    public void SetAudio(bool muted, int volume)
    {
        lock (_audioGate)
        {
            if (_disposed) return;
            _muted = muted; _volume = Math.Clamp(volume, 0, 100);
            ApplyAudioLocked();
        }
    }
    public void SetAudioOutputAvailable(bool available)
    {
        lock (_audioGate)
        {
            if (_disposed) return;
            _outputAvailable = available;
            ApplyAudioLocked();
        }
    }
    // Every native audio write, including delayed callbacks, reads the latest
    // intent under this lock. Do not hold it while stopping the native player.
    private void ApplyAudioLocked()
    {
        if (_disposed) return;
        try
        {
            var muted = !Ready || _muted || _audioHeld || !_hasAudioTrack || !_outputAvailable;
            if (muted) Player.Mute = true;
            Player.Volume = _volume;
            if (!muted) Player.Mute = false;
        }
        catch (Exception ex) { SetAudioErrorLocked("音频输出失败：" + ex.Message); }
    }
    private void SetAudioErrorLocked(string? error)
    {
        if (_audioError == error) return;
        _audioError = error;
        if (error != null) Log.Write($"Audio {_item.Id}: {error}");
        Changed?.Invoke();
    }
    public AudioSnapshot GetAudioSnapshot()
    {
        lock (_audioGate)
        {
            if (_disposed) return new(_muted, null, _volume, null, _hasAudioTrack, 0, false, false, _audioError);
            var volume = Player.Volume;
            return new(_muted, Ready && _hasAudioTrack && volume >= 0 ? Player.Mute : null, _volume,
                volume >= 0 ? volume : null, _hasAudioTrack, _media.Statistics.PlayedAudioBuffers,
                Ready, Player.IsPlaying, _audioError);
        }
    }
    public async Task RebindDefaultAudioDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (_audioGate)
                {
                    if (_disposed) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    _audioHeld = true; ApplyAudioLocked();
                    try
                    {
                        // libVLC 3 ignores a NULL device. An empty string selects
                        // the default DirectSound device and restarts only aout.
                        Player.SetOutputDevice(null!, "");
                        _deviceGeneration++;
                        _fallbackUsed = false;
                        SetAudioErrorLocked(null);
                        var generation = _deviceGeneration;
                        _audioWatch = Task.Run(() => WatchAudioAsync(generation));
                    }
                    catch (Exception ex) { SetAudioErrorLocked("音频设备恢复失败：" + ex.Message); }
                    finally { _audioHeld = false; ApplyAudioLocked(); }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
    private void EnsureAudioWatchLocked()
    {
        if (_hasAudioTrack && (_audioWatch == null || _audioWatch.IsCompleted))
        {
            var generation = _deviceGeneration;
            _audioWatch = Task.Run(() => WatchAudioAsync(generation));
        }
    }
    private async Task WatchAudioAsync(long deviceGeneration)
    {
        int lastBuffers = -1;
        long stalledSince = 0;
        try
        {
            while (true)
            {
                await Task.Delay(200, _lifetimeToken).ConfigureAwait(false);
                AudioSnapshot state;
                lock (_audioGate)
                {
                    if (_disposed || deviceGeneration != _deviceGeneration || Error != null) return;
                    state = GetAudioSnapshot();
                    if (!state.HasAudioTrack) return;
                    if (!_outputAvailable || !state.Ready || !state.Playing)
                    { lastBuffers = state.PlayedAudioBuffers; stalledSince = 0; continue; }
                    if (lastBuffers >= 0 && state.PlayedAudioBuffers > lastBuffers)
                    { SetAudioErrorLocked(null); return; }
                    if (lastBuffers >= 0 && state.PlayedAudioBuffers < lastBuffers)
                    { lastBuffers = state.PlayedAudioBuffers; stalledSince = 0; continue; }
                    lastBuffers = state.PlayedAudioBuffers;
                    if (stalledSince == 0) stalledSince = Environment.TickCount64;
                    if (Environment.TickCount64 - stalledSince < 2000) continue;
                    if (_fallbackUsed)
                    { SetAudioErrorLocked("音频输出初始化失败，视频可继续播放；请检查输出设备。"); return; }
                }
                await ExecuteOffThread(() =>
                {
                    lock (_audioGate)
                    {
                        if (deviceGeneration != _deviceGeneration || !_outputAvailable || !_playRequested) return;
                        _fallbackUsed = true;
                        _audioHeld = true; ApplyAudioLocked();
                    }
                    try
                    {
                        var position = PositionMs;
                        var paused = !_playRequested;
                        Player.Stop();
                        Log.Write($"Audio {_item.Id}: restarting input once after output stalled");
                        BeginInput(position, paused);
                        lock (_audioGate) _audioRecoveryRestarts++;
                    }
                    catch (Exception ex)
                    { lock (_audioGate) SetAudioErrorLocked("音频恢复失败：" + ex.Message); }
                    finally { lock (_audioGate) { _audioHeld = false; ApplyAudioLocked(); } }
                }).ConfigureAwait(false);
                lastBuffers = -1; stalledSince = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_audioGate) if (!_disposed) SetAudioErrorLocked("音频恢复检查失败：" + ex.Message);
        }
    }
    public void SetRate(float rate) { lock (_audioGate) { _item.Rate = rate; if (!_disposed) Player.SetRate(rate); } }
    public async ValueTask DisposeAsync()
    {
        lock (_audioGate)
        {
            if (_disposed) return;
            _audioHeld = true; ApplyAudioLocked();
            _disposed = true; Ready = false;
            Interlocked.Increment(ref _generation);
            _deviceGeneration++;
        }
        _lifetime.Cancel();
        await _commands.WaitAsync();
        try
        {
            Player.Playing -= OnPlaying; Player.EncounteredError -= OnError; Player.EndReached -= OnEnded;
            Player.Stop(); Player.Dispose(); _media.Dispose();
        }
        finally { _commands.Release(); _lifetime.Dispose(); }
    }
}
