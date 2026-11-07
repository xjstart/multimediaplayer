namespace MultiMediaPlayer.App.Media;

public readonly record struct AudioSnapshot(bool DesiredMuted, bool? NativeMuted, int DesiredVolume,
    int? NativeVolume, bool HasAudioTrack, int PlayedAudioBuffers, bool Ready, bool Playing, string? Error);
