namespace MultiMediaPlayer.App.Media;
public interface IMediaSession : IAsyncDisposable
{
    long PositionMs { get; }
    long DurationMs { get; }
    bool IsPlaying { get; }
    event Action? Changed;
    Task PlayAsync();
    Task PauseAsync();
    Task SeekAsync(long milliseconds);
    void SetAudio(bool muted, int volume);
}
