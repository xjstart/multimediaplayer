using System.IO;
using System.Text.Json;
using System.Windows;
using LibVLCSharp.Shared;
using MultiMediaPlayer.App;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.AudioTests;

internal static class Program
{
    private static readonly int[] Frequencies = [440, 617, 797, 997, 1213, 1451, 1733, 1999, 2333];
    private static readonly List<object> Checks = [];
    private static readonly List<object> Measurements = [];
    private static string _fixtures = "";
    private static double _reference;
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) => { result = await RunAsync(args); app.Shutdown(result); };
        app.Run();
        return result;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition, string message, int milliseconds = 15000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!condition()) { Require(Environment.TickCount64 < end, message); await Task.Delay(50); }
    }
    private static string Option(string[] args, string key, string fallback)
    { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
    private static void Passed(string name)
    { Checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    private static async Task<int> RunAsync(string[] args)
    {
        var output = Path.GetFullPath(Option(args, "--output", "artifacts/audio-tests.json"));
        _fixtures = Path.GetFullPath(Option(args, "--fixtures", "artifacts/audio-fixtures"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string status = "failed"; string? error = null; int exit = 1;
        try
        {
            LibVLCSharp.Shared.Core.Initialize(Path.GetFullPath(Option(args, "--native-path", "src/MultiMediaPlayer.App/bin/Release/net10.0-windows/win-x64/libvlc/win-x64")));
            await TestNotificationsAsync();
            using var devices = new DefaultAudioDeviceMonitor();
            if (!devices.Current.Available) throw new NotSupportedException("No default audio output device: loopback verification was not performed.");
            using var capture = await LoopbackProbe.CreateAsync();
            await TestSessionsAsync(capture);
            await TestWindowAsync(capture);
            status = "passed"; exit = 0;
        }
        catch (NotSupportedException ex) { status = "unverified"; error = ex.ToString(); exit = 2; Console.Error.WriteLine(error); }
        catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(error); }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status, passed = exit == 0, checks = Checks,
            measurements = Measurements, error, manualDeviceHotplug = "not performed; requires physical devices" }, new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }
    private static async Task TestNotificationsAsync()
    {
        var state = new AudioDeviceState(true, "initial");
        using var monitor = new DefaultAudioDeviceMonitor(() => state);
        var changes = new List<AudioDeviceState>();
        monitor.Changed += s => { lock (changes) changes.Add(s); };
        state = new(false, null);
        for (int i = 0; i < 20; i++) monitor.NotifyDeviceChanged();
        await Task.Delay(450);
        lock (changes) Require(changes.Count == 1 && !changes[0].Available, "Notifications were not debounced.");
        state = new(true, "replacement"); monitor.NotifyDeviceChanged();
        await Task.Delay(450);
        lock (changes) Require(changes.Count == 2 && changes[1].Id == "replacement", "Device availability did not recover.");
        state = new(false, null); monitor.NotifyDeviceChanged(); monitor.Dispose();
        await Task.Delay(450);
        lock (changes) Require(changes.Count == 2, "Disposed notification monitor delivered a pending event.");
        Passed("device notifications: debounce, disappearance, reappearance, cancellation on disposal");
    }
    private static void Route(IReadOnlyList<VlcSession> sessions, int selected, bool muted = false, int volume = 50)
    {
        foreach (var s in sessions) s.SetAudio(true, volume);
        if (selected >= 0) sessions[selected].SetAudio(muted, volume);
    }
    private static async Task CheckOutputAsync(LoopbackProbe capture, int selected, string name)
    {
        await Task.Delay(200);
        var magnitudes = await capture.MeasureAsync(Frequencies);
        Measurements.Add(new { name, selected, magnitudes });
        if (selected >= 0)
        {
            var target = magnitudes[selected];
            Require(target > 1e-6, $"{name}: selected tone is missing ({target:E2}).");
            _reference = Math.Max(_reference, target);
            for (int i = 0; i < magnitudes.Length; i++)
                if (i != selected) Require(magnitudes[i] <= target / Math.Pow(10, 30d / 20),
                    $"{name}: unwanted {Frequencies[i]} Hz, target={target:E2}, unwanted={magnitudes[i]:E2}.");
        }
        else
        {
            Require(_reference > 0, "A silent capture cannot establish that loopback is working.");
            Require(magnitudes.All(m => m < _reference / Math.Pow(10, 30d / 20)), $"{name}: audio leaked while silence was expected.");
        }
        Passed(name);
    }
    private static async Task TestSessionsAsync(LoopbackProbe capture)
    {
        using var engine = VlcEngine.Create("--vout=dummy");
        var sessions = new List<VlcSession>();
        VlcSession Create(string file) => new(engine, new MediaItem { Path = Path.Combine(_fixtures, file), HardwareDecoding = false });
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var s = Create($"{Frequencies[i]}.mp4"); sessions.Add(s);
                await s.StartAsync(0, false);
            }
            Route(sessions, 0); Route(sessions, 2);
            await Until(() => sessions.All(s => s.Ready && s.PositionMs > 300), "Three sessions did not start.");
            Measurements.Add(new { name = "startup native state", states = sessions.Select(s => s.GetAudioSnapshot()).ToArray() });
            await CheckOutputAsync(capture, 2, "startup selection uses latest audio intent (3 videos)");
            for (int i = 0; i < 100; i++) { Route(sessions, i % 3); await Task.Delay(15); }
            Route(sessions, 0);
            await CheckOutputAsync(capture, 0, "100 rapid focus switches (3 videos)");
            for (int i = 3; i < 9; i++)
            {
                var s = Create($"{Frequencies[i]}.mp4"); sessions.Add(s); await s.StartAsync(0, false);
            }
            await Until(() => sessions.All(s => s.Ready && s.PositionMs > 300), "Nine sessions did not start.");
            for (int i = 0; i < 100; i++) { Route(sessions, i % 9); await Task.Delay(15); }
            for (int i = 0; i < 9; i++)
            {
                Route(sessions, i);
                await CheckOutputAsync(capture, i, $"nine videos: native/WASAPI exclusive focus {i}");
                Require(sessions.Count(s => s.GetAudioSnapshot().NativeMuted == false) == 1, "Native mute differs from audio policy.");
            }
            Route(sessions, 0, true); await CheckOutputAsync(capture, -1, "global mute");
            Route(sessions, 0, false, 0); await CheckOutputAsync(capture, -1, "volume zero");
            Route(sessions, 0); await CheckOutputAsync(capture, 0, "volume restoration");
            foreach (var s in sessions.Skip(1)) await s.PauseAsync();
            await sessions[1].PlayAsync(); await sessions[1].PauseAsync();
            await CheckOutputAsync(capture, 0, "background resume/pause and delayed callback preserve selected sound");
            await sessions[0].PauseAsync(); await CheckOutputAsync(capture, -1, "paused focus stays silent without following another video");
            await sessions[0].PlayAsync(); await CheckOutputAsync(capture, 0, "resume focused video");
            var handles = sessions.Select(s => s.Player.NativeReference).ToArray();
            var position = sessions[0].PositionMs;
            Route(sessions, -1);
            var rebind = Task.WhenAll(sessions.Select(s => s.RebindDefaultAudioDeviceAsync()));
            Route(sessions, 2); await rebind;
            await sessions[2].PlayAsync();
            await CheckOutputAsync(capture, 2, "device rebind respects selection changed during recovery");
            Require(handles.SequenceEqual(sessions.Select(s => s.Player.NativeReference)) && sessions[0].PositionMs >= position,
                "Audio-only recovery replaced a player or reset its video position.");
            Require(sessions.Skip(3).All(s => !s.IsPlaying), "Device recovery resumed paused videos.");
            Passed("audio-only recovery preserves player handles, positions and pause state");
            // Inject a stalled audio decoder without changing any system device.
            // Rebinding starts the production progress watchdog; it must reopen
            // the input once and recover the track while preserving the video.
            Route(sessions, 2);
            await Until(() => sessions[2].GetAudioSnapshot().PlayedAudioBuffers > 10, "Audio statistics did not advance.");
            Require(sessions[2].Player.SetAudioTrack(-1), "Could not inject stalled audio.");
            await Task.Delay(300);
            await sessions[2].RebindDefaultAudioDeviceAsync();
            await Until(() => sessions[2].AudioRecoveryRestarts == 1, "Stalled audio did not trigger the single fallback.", 5000);
            await Until(() => sessions[2].Ready && sessions[2].GetAudioSnapshot().PlayedAudioBuffers > 5,
                "Fallback did not restore audio buffers.", 5000);
            await CheckOutputAsync(capture, 2, "stalled audio fallback restores the selected track");
            Require(sessions[2].Player.NativeReference == handles[2] && sessions[2].Error == null,
                "Audio fallback replaced the player or misreported a file error.");
            Require(sessions[2].AudioRecoveryRestarts == 1, "Fallback restarted more than once.");
            // A paused session's deferred check must also bound persistent
            // failure: disable the track again immediately after its fallback.
            await sessions[3].RebindDefaultAudioDeviceAsync();
            await Task.Delay(2300);
            Require(sessions[3].AudioRecoveryRestarts == 0, "Paused video was restarted by the watchdog.");
            Require(sessions[3].Player.SetAudioTrack(-1), "Could not inject deferred audio failure.");
            await sessions[3].PlayAsync();
            await Until(() => sessions[3].AudioRecoveryRestarts == 1, "Deferred recovery did not run.", 5000);
            await Until(() => sessions[3].Ready && sessions[3].Player.AudioTrackDescription.Any(t => t.Id >= 0), "Deferred fallback did not finish.");
            Require(sessions[3].Player.SetAudioTrack(-1), "Could not inject persistent audio failure.");
            await Until(() => sessions[3].GetAudioSnapshot().Error != null, "Persistent audio error was not reported.", 5000);
            Require(sessions[3].AudioRecoveryRestarts == 1 && sessions[3].Error == null && sessions[3].IsPlaying,
                "Persistent audio failure restarted repeatedly or stopped video playback.");
            var track = sessions[3].Player.AudioTrackDescription.First(t => t.Id >= 0).Id;
            Require(sessions[3].Player.SetAudioTrack(track), "Could not restore the injected track.");
            await sessions[3].RebindDefaultAudioDeviceAsync();
            await Until(() => sessions[3].GetAudioSnapshot().PlayedAudioBuffers > 5, "Paused output did not recover on resume.");
            Passed("deferred recovery, one fallback per device, separate audio error, recovery on later device event");
            Require(sessions[4].Player.SetAudioTrack(-1), "Could not inject no-device audio stall.");
            sessions[4].SetAudioOutputAvailable(false);
            await sessions[4].PlayAsync(); await sessions[4].RebindDefaultAudioDeviceAsync();
            var noDevicePosition = sessions[4].PositionMs;
            await Task.Delay(2500);
            Require(sessions[4].AudioRecoveryRestarts == 0 && sessions[4].PositionMs > noDevicePosition,
                "No-device state restarted or stopped the video.");
            sessions[4].SetAudioOutputAvailable(true); await sessions[4].RebindDefaultAudioDeviceAsync();
            await Until(() => sessions[4].AudioRecoveryRestarts == 1 && sessions[4].Ready,
                "Device return did not recover a previously stalled decoder.", 5000);
            Passed("no-device state waits without restarting video; device return recovers stalled decoder");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await sessions[0].RebindDefaultAudioDeviceAsync(cancel.Token); throw new Exception("Cancelled rebind ran."); }
                catch (OperationCanceledException) { }
            }
            var silent = Create("silent.mp4"); sessions.Add(silent); await silent.StartAsync(0, false);
            await Until(() => silent.Ready, "Silent video did not start."); Route(sessions, 9);
            await CheckOutputAsync(capture, -1, "video without an audio track");
            Require(!silent.GetAudioSnapshot().HasAudioTrack, "Silent video was reported as having audio.");
            await silent.DisposeAsync(); sessions.Remove(silent);
            await sessions[8].DisposeAsync(); sessions[8] = Create("loop.mp4"); await sessions[8].StartAsync(0, false);
            Route(sessions, 0);
            for (int i = 0; i < 4; i++) { await Task.Delay(1000); await CheckOutputAsync(capture, 0, "background loop does not steal audio " + i); }
            Route(sessions, 8);
            await Until(() => sessions[8].Ready && sessions[8].PositionMs is > 300 and < 850, "Loop did not restart.");
            await CheckOutputAsync(capture, 8, "looping selected video restores audio");
            var disposed = sessions[8]; await disposed.PlayAsync(); await disposed.DisposeAsync(); sessions.RemoveAt(8);
            disposed.SetAudio(false, 90);
            Require(disposed.GetAudioSnapshot().NativeMuted == null, "Disposed player still exposes native audio.");
            Passed("dispose invalidates delayed callbacks and ignores later audio writes");
            var starting = Create("440.mp4");
            starting.SetAudio(false, 50);
            await starting.StartAsync(30000, true);
            await Task.Delay(150);
            await starting.DisposeAsync();
            await Task.Delay(200);
            Require(!starting.Ready && starting.GetAudioSnapshot().NativeMuted == null,
                "A callback from the released input restored readiness.");
            await CheckOutputAsync(capture, -1, "disposing during startup/seek invalidates pending callbacks and stays silent");
        }
        finally { foreach (var s in sessions) await s.DisposeAsync(); }
    }
    private static async Task TestWindowAsync(LoopbackProbe capture)
    {
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = 0 };
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        try
        {
            await window.ImportAsync(Frequencies.Take(3).Select(f => Path.Combine(_fixtures, $"{f}.mp4")));
            await Until(() => window.Tiles.All(t => t.Session?.Ready == true), "WPF sessions did not start.");
            window.DiagnosticVolume(50); window.Select(window.Tiles[0]);
            await CheckOutputAsync(capture, 0, "WPF audio routing");
            await window.ImportAsync([Path.Combine(_fixtures, "image.png")]);
            await CheckOutputAsync(capture, -1, "selected image silences every video");
            window.Select(window.Tiles[1]);
            var snapshot = window.Tiles[1].Session!.PositionMs;
            await window.ReplaceTileAsync(window.Tiles[1], false);
            await Until(() => window.Tiles[1].Session?.Ready == true, "Replacement did not start.");
            await CheckOutputAsync(capture, 1, "replacing selected player restores only its audio");
            var workspace = Path.GetFullPath("artifacts/audio-workspace.mmp.json");
            await window.SaveWorkspaceAsync(workspace); await window.OpenWorkspaceAsync(workspace);
            await Until(() => window.Tiles.Where(t => t.Session != null).All(t => t.Session!.Ready), "Workspace did not restore.");
            Require(window.Tiles.Where(t => t.Session != null).All(t => !t.Session!.IsPlaying), "Workspace resumed paused media.");
            await CheckOutputAsync(capture, -1, "workspace restore stays paused and silent");
            await window.Tiles[1].Session!.PlayAsync();
            await CheckOutputAsync(capture, 1, "restored workspace resumes selected audio");
            await window.RecoverAudioDeviceAsync(new(false, null));
            var unavailablePosition = window.Tiles[1].Session!.PositionMs;
            await CheckOutputAsync(capture, -1, "unavailable output suspends audio while video advances");
            Require(window.Tiles[1].Session!.PositionMs > unavailablePosition, "No-device state paused the video.");
            var recovery = window.RecoverAudioDeviceAsync(new(true, "simulated-replacement"));
            window.Select(window.Tiles[2]); await window.Tiles[2].Session!.PlayAsync(); await recovery;
            await CheckOutputAsync(capture, 2, "device return follows latest selection");
            await window.RemoveAsync(window.Tiles[2]);
            window.Select(window.Tiles[0]); await window.Tiles[0].Session!.PlayAsync();
            await CheckOutputAsync(capture, 0, "removing selected video cannot leave its audio running");
            await window.ImportAsync([Path.Combine(_fixtures, "797.mp4")]);
            await Until(() => window.Tiles[^1].Session?.Ready == true, "Re-added video did not start.");
            await CheckOutputAsync(capture, 2, "re-adding a video routes audio to the new player");
            var pending = window.RecoverAudioDeviceAsync(new(true, "closing-replacement"));
            window.Close(); await pending; await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Passed("close cancels device recovery and disposes native audio safely");
        }
        finally { if (!closed.Task.IsCompleted) { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15)); } }
    }
}
