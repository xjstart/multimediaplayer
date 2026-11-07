using LibVLCSharp.Shared;

namespace MultiMediaPlayer.App.Media;

public static class VlcEngine
{
    public static LibVLC Create(params string[] additionalOptions)
    {
        // The mmdevice backend controls a shared Windows session. DirectSound
        // controls each player's buffer instead. Never fall back to mmdevice.
        var engine = new LibVLC(new[] { "--ignore-config", "--no-video-title-show", "--no-snapshot-preview",
            "--no-osd", "--aout=directsound,none", "--no-volume-save", "--directx-volume=0", "--no-spdif", "--quiet" }
            .Concat(additionalOptions).ToArray());
        Log.Write("Audio backend: directsound (PCM, independent player buffers)");
        return engine;
    }
}
