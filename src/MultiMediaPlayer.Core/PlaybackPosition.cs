namespace MultiMediaPlayer.Core;

public static class PlaybackPosition
{
    public static long Offset(long position, long delta, long duration)
    {
        var last = duration > 0 ? duration - 1 : 0;
        var current = Math.Clamp(position, 0, last);
        return current + Math.Clamp(delta, -current, last - current);
    }
}
