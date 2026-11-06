using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App;

/// <summary>Opt-in test harness. Uses the production WPF views and native decoders, never user media discovery.</summary>
internal static class Diagnostics
{
    private static async Task Until(Func<bool> test, string reason, int timeoutSeconds = 20)
    {
        var timer = Stopwatch.StartNew();
        while (!test()) { if (timer.Elapsed.TotalSeconds > timeoutSeconds) throw new Exception(reason); await Task.Delay(100); }
    }
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static string Option(string[] args, string key, string fallback)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
    public static async Task RunAsync(MainWindow window, string[] args)
    {
        var output = Path.GetFullPath(Option(args, "--output", "artifacts/diagnostics.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var checks = new List<string>(); var metrics = new List<object>();
        try
        {
            var file = Path.GetFullPath(Option(args, "--media", "artifacts/fixtures/landscape.mp4"));
            var count = int.Parse(Option(args, "--count", "4"));
            window.DiagnosticMute(true);
            var media = Enumerable.Repeat(file, count).ToArray();
            var secondary = Option(args, "--secondary-media", "");
            if (secondary.Length > 0) media[^1] = Path.GetFullPath(secondary);
            await window.ImportAsync(media);
            await Until(() => window.Tiles.Count == count && window.Tiles.All(t => t.Session?.Ready == true), "Decoders did not become ready.");
            await Until(() => window.Tiles.All(t => t.Session!.PositionMs > 300), "Playback did not advance.");
            checks.Add($"{count} video sessions rendered and advanced");
            if (args.Contains("--smoke"))
            {
                var handles = window.Tiles.Select(t => t.Session!.Player.NativeReference).ToArray();
                foreach (var mode in new[] { LayoutMode.Grid, LayoutMode.Focus, LayoutMode.Automatic })
                { window.DiagnosticMode(mode); await Task.Delay(400); }
                Require(handles.SequenceEqual(window.Tiles.Select(t => t.Session!.Player.NativeReference)), "Layout recreated a decoder."); checks.Add("layout changes preserve decoder instances");
                var first = window.Tiles[0];
                var target = window.Tiles[^1];
                window.Reposition(first.Item.Id, target.Item.Id, TilePlacement.Above);
                var split = window.CurrentLayout?.ParentOf(first.Item.Id);
                Require(split?.Axis == SplitAxis.Vertical && split.First?.ItemId == first.Item.Id &&
                    split.Second?.ItemId == target.Item.Id, "Directional move did not stack both videos.");
                var beforeZoom = first.Session!.PositionMs;
                first.ChangeZoom(.25);
                first.Item.PanX = .5;
                first.ChangeZoom(.25);
                await Until(() => first.Session.PositionMs > beforeZoom + 100, "Video did not advance after zoom and pan.");
                Require(first.EffectiveZoom == 1.5 && handles.SequenceEqual(window.Tiles.Select(t => t.Session!.Player.NativeReference)),
                    "Layout or zoom replaced a player, or failed to apply the requested zoom.");
                checks.Add("directional layout, zoom and pan preserve active players");
                window.DiagnosticMute(false);
                window.Select(window.Tiles[0]);
                Require(window.Tiles.Count(t => !t.Session!.IsMuted) == 1, "More than one audio session is enabled.");
                window.Select(window.Tiles[^1]);
                Require(window.Tiles[^1].Session!.IsMuted == false && window.Tiles.Take(count - 1).All(t => t.Session!.IsMuted), "Audio focus did not switch.");
                window.DiagnosticMute(true); checks.Add("exclusive audio focus and global mute");
                foreach (var t in window.Tiles) await t.Session!.PauseAsync();
                await Until(() => window.Tiles.All(t => !t.Session!.IsPlaying), "Pause failed.");
                await window.Tiles[0].Session!.SeekAsync(2000);
                await Until(() => Math.Abs(window.Tiles[0].Session!.PositionMs - 2000) < 350, "Seek failed.");
                var otherPosition = target.Session!.PositionMs;
                await Task.WhenAll(first.Session.SeekRelativeAsync(5000), first.Session.SeekRelativeAsync(-5000),
                    first.Session.SeekRelativeAsync(5000));
                await Until(() => Math.Abs(first.Session.PositionMs - 7000) < 350, "Relative seeks did not accumulate.");
                Require(!first.Session.IsPlaying && Math.Abs(target.Session.PositionMs - otherPosition) < 350,
                    "Relative seek resumed playback or changed another video.");
                await first.Session.SeekAsync(2000);
                await Until(() => Math.Abs(first.Session.PositionMs - 2000) < 350, "Seek reset failed.");
                window.Tiles[0].Session!.SetRate(1.5f);
                checks.Add("pause, absolute and relative seek, and playback rate");
                window.Reposition(window.Tiles[0].Item.Id, window.Tiles[^1].Item.Id, TilePlacement.Center);
                window.DiagnosticRatio(.6); await Task.Delay(500);
                var original = window.CurrentLayout!.Copy();
                var work = output + ".mmp.json";
                await window.SaveWorkspaceAsync(work);
                await window.OpenWorkspaceAsync(work);
                await Until(() => window.Tiles.All(t => t.Session?.Ready == true), "Workspace sessions did not restore.");
                await Task.Delay(400);
                Require(window.Tiles.All(t => !t.Session!.IsPlaying), "Workspace autoplayed.");
                Require(Math.Abs(window.Tiles[0].Session!.PositionMs - 2000) < 500, "Saved seek position was not restored.");
                Require(window.Tiles[0].Item.Rate == 1.5f, "Saved playback rate was not restored.");
                Require(window.Tiles[0].Item.Zoom == 1.5 && window.Tiles[0].Item.PanX == .5 &&
                    window.Tiles[0].EffectiveZoom == 1.5, "Saved zoom or pan was not restored.");
                Require(window.CurrentLayout?.Ratio == original.Ratio && window.CurrentMode == LayoutMode.Custom, "Custom ratio was lost.");
                checks.Add("workspace persists layout and reopens paused");
                window.Width = 940; window.Height = 520; await Task.Delay(400);
                Require(window.CurrentLayout?.Ratio == original.Ratio, "Resize lost split ratio.");
                window.ToggleFullscreen(); await Task.Delay(300); window.ToggleFullscreen();
                checks.Add("resize and fullscreen preserve custom ratio");
                var last = window.Tiles[^1]; await window.RemoveAsync(last);
                await window.ImportAsync([file], true);
                await Until(() => window.Tiles.Count == count && window.Tiles.All(t => t.Session?.Ready == true), "Remove/add did not recover.");
                checks.Add("remove and re-add releases/recreates sessions");
            }
            else
            {
                int duration = int.Parse(Option(args, "--seconds", "1800"));
                var process = Process.GetCurrentProcess(); var watch = Stopwatch.StartNew();
                var previousCpu = process.TotalProcessorTime; var previousTime = watch.Elapsed;
                var previousStats = window.Tiles.Select(t => t.Session!.Statistics).ToArray();
                long displayed = 0, lost = 0;
                await File.WriteAllTextAsync(output + ".jsonl", "");
                while (watch.Elapsed.TotalSeconds < duration)
                {
                    await Task.Delay(Math.Min(5000, Math.Max(100, (duration - (int)watch.Elapsed.TotalSeconds) * 1000)));
                    Require(window.Tiles.All(t => t.Session!.Error == null), "A decoder encountered an error.");
                    process.Refresh(); var elapsed = watch.Elapsed; var cpu = process.TotalProcessorTime;
                    for (int j = 0; j < window.Tiles.Count; j++)
                    {
                        var s = window.Tiles[j].Session!.Statistics; var p = previousStats[j];
                        displayed += s.Displayed >= p.Displayed ? s.Displayed - p.Displayed : s.Displayed;
                        lost += s.Lost >= p.Lost ? s.Lost - p.Lost : s.Lost; previousStats[j] = s;
                    }
                    var sample = new { seconds = elapsed.TotalSeconds, cpuPercent = (cpu - previousCpu).TotalSeconds / (elapsed - previousTime).TotalSeconds / Environment.ProcessorCount * 100,
                        workingSetMB = process.WorkingSet64 / 1048576d, privateMB = process.PrivateMemorySize64 / 1048576d, displayed, lost, positions = window.Tiles.Select(t => t.Session!.PositionMs).ToArray() };
                    metrics.Add(sample); await File.AppendAllTextAsync(output + ".jsonl", JsonSerializer.Serialize(sample) + "\n");
                    window.Status($"验证中 · {count} 路 · {elapsed:mm\\:ss} / {TimeSpan.FromSeconds(duration):mm\\:ss} · CPU {sample.cpuPercent:0.0}% · 内存 {sample.privateMB:0} MB");
                    previousCpu = cpu; previousTime = elapsed;
                }
                Require(displayed > count * duration * 10, "Rendered frame throughput is too low.");
                checks.Add($"continuous playback for {duration} seconds; displayed={displayed}, lost={lost}");
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = true, checks, metrics }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString(), metrics }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        window.Close();
    }
    public static async Task VerifyWorkspaceAsync(MainWindow window, string[] args)
    {
        string path = Path.GetFullPath(Option(args, "--verify-workspace", ""));
        string output = Path.GetFullPath(Option(args, "--output", "artifacts/workspace-result.json"));
        try
        {
            await window.OpenWorkspaceAsync(path);
            await Until(() => window.Tiles.Count > 0 && window.Tiles.All(t => t.Item.Kind == MediaKind.Image || t.Error != null || t.Session?.Ready == true), "Workspace startup did not finish.");
            await Task.Delay(300);
            var tiles = window.Tiles.Select(t => new
            {
                file = Path.GetFileName(t.Item.Path), t.Item.DisplayAspect, t.Item.PositionMs, t.Error,
                actualPosition = t.Session?.PositionMs, isPlaying = t.Session?.IsPlaying, videoOutputCount = t.Session?.Player.VoutCount
            }).ToArray();
            int expectedErrors = int.Parse(Option(args, "--expected-errors", "0"));
            bool passed = tiles.Count(t => t.Error != null) == expectedErrors
                && tiles.All(t => t.isPlaying != true && (t.actualPosition == null || t.Error != null
                    || t.videoOutputCount > 0 && Math.Abs(t.actualPosition.Value - t.PositionMs) < 500));
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed, tiles }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); }
        window.Close();
    }
    public static async Task VerifyImagesAsync(MainWindow window, string[] args)
    {
        int i = Array.IndexOf(args, "--directory"); var directory = Path.GetFullPath(args[i + 1]);
        var results = new List<object>(); bool passed = true;
        foreach (var path in Directory.GetFiles(directory).Where(p => MediaFiles.Images.Contains(Path.GetExtension(p))))
        {
            try
            {
                var image = await Task.Run(() => ImageLoader.Load(path));
                results.Add(new { file = Path.GetFileName(path), width = image.Bitmap.PixelWidth, height = image.Bitmap.PixelHeight, aspect = image.Aspect });
                if (Path.GetFileName(path).StartsWith("exif-") && image.Aspect >= 1) passed = false;
            }
            catch (Exception e) { passed = false; results.Add(new { file = path, error = e.Message }); }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "images-result.json"), JsonSerializer.Serialize(new { passed, results }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
