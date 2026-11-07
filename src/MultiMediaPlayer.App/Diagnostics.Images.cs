using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MultiMediaPlayer.App.Controls;
using MultiMediaPlayer.App.Media;
using MultiMediaPlayer.Core;
using SkiaSharp;

namespace MultiMediaPlayer.App;

internal static partial class Diagnostics
{
    public static async Task VerifyImageNavigationAsync(MainWindow window, string[] args)
    {
        var output = Path.GetFullPath(Option(args, "--output", "artifacts/image-navigation.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var directory = Path.GetFullPath("artifacts/image-navigation-fixtures");
        var checks = new List<string>();
        object? memory = null;
        try
        {
            Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.Combine(directory, "子目录"));
            string P(string name) => Path.Combine(directory, name);
            void Make(string name, int width, int height)
            {
                using var bitmap = new SKBitmap(width, height);
                using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(width > height ? SKColors.SteelBlue : SKColors.DarkOliveGreen); }
                using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(P(name)); data.SaveTo(stream);
            }
            Make("图1.png", 800, 400); Make("图2.png", 400, 800); Make("图4.png", 700, 700); Make("图10.png", 800, 400);
            Make("子目录/图20.png", 400, 800); await File.WriteAllTextAsync(P("图3.png"), "invalid image");
            await File.WriteAllTextAsync(P("忽略.txt"), "not media");
            var flat = await ImageDirectory.ReadAsync(directory); var recursive = await ImageDirectory.ReadAsync(directory, true);
            Require(flat.Paths.Count == 5 && recursive.Paths.Count == 6 && flat.Paths.Select(Path.GetFileName).SequenceEqual(new[] { "图1.png", "图2.png", "图3.png", "图4.png", "图10.png" }), "Directory enumeration/natural ordering failed.");
            var inaccessible = await ImageDirectory.ReadAsync(P("不存在的目录"));
            Require(inaccessible.Errors.Count == 1, "Inaccessible directory must be reported.");
            checks.Add("directory natural order, format filter, recursion, unavailable directory reporting");

            await VerifyImageRequestOrderingAsync(checks, P("图1.png"), P("图2.png"), P("图4.png"));
            var video = Path.GetFullPath(Option(args, "--media", "artifacts/fixtures/720p.mp4"));
            await window.ImportAsync([video, .. Enumerable.Repeat(P("图1.png"), 8)]);
            await Until(() => window.Tiles.Count == 9 && window.Tiles[0].Session?.Ready == true, "Mixed workspace did not load.");
            await Task.Delay(500);
            var tile = window.Tiles[1]; var browser = tile.Browser!; var independent = window.Tiles[2];
            var handle = window.Tiles[0].Session!.Player.NativeReference;
            window.Select(tile);
            Box[] Bounds() => window.Tiles.Select(t => new Box(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width, t.Height)).ToArray();
            var bounds = Bounds(); var id = tile.Item.Id;
            tile.Item.Fit = FitMode.Cover; tile.Item.Zoom = 2; tile.Item.PanX = .5; tile.Item.PanY = -.5;
            Require(await browser.NavigateAsync(1) && tile.Item.Path == P("图2.png") && tile.Item.DisplayAspect == .5 && tile.Item.Zoom == 1 && tile.Item.PanX == 0 && tile.Item.Fit == FitMode.Cover, "Navigation did not reset viewport/preserve fit.");
            Require(await browser.NavigateAsync(1) && tile.Item.Path == P("图4.png") && browser.Errors.ContainsKey(P("图3.png")), "Corrupt image was not skipped.");
            Require(await browser.NavigateAsync(1) && tile.Item.Path == P("图10.png") && !await browser.NavigateAsync(1), "Final boundary wrapped.");
            Require(independent.Item.Path == P("图1.png") && tile.Item.Id == id, "Independent view or identity changed.");
            await Task.Delay(300);
            Require(Bounds().SequenceEqual(bounds), "Image navigation changed layout geometry.");
            Require(window.Tiles[0].Session!.Player.NativeReference == handle && window.Tiles[0].Session!.IsPlaying && window.Tiles[0].Session!.GetAudioSnapshot().NativeMuted != false, "Navigation recreated/stopped/unmuted video.");
            checks.Add("nine-view mixed workspace, aspect/viewport reset, corrupt skip, boundaries, independent views, stable geometry and native video");

            var custom = new List<string> { P("不存在.png"), P("图2.png"), P("图3.png"), P("图10.png") };
            Require(await browser.ApplyAsync(custom), "Custom collection apply failed.");
            custom.Clear();
            Require(tile.Item.ImageCollectionPaths!.Count == 4, "Draft aliases committed collection.");
            Require(await browser.NavigateAsync(-1) && tile.Item.Path == P("图2.png"), "Reverse skip failed.");
            var beforeFailure = tile.Item.Copy();
            Require(!await browser.ApplyAsync([P("图3.png"), P("不存在.png")]) && tile.Item.Path == beforeFailure.Path && tile.Item.ImageCollectionPaths!.SequenceEqual(beforeFailure.ImageCollectionPaths!), "Failed apply partially committed.");
            var beforeDialog = tile.Item.Copy();
            var dialog = new ImageCollectionDialog(tile.Item, browser) { Owner = window };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(dialog.Close);
            dialog.ShowDialog();
            Require(tile.Item.ImageCollectionPaths!.SequenceEqual(beforeDialog.ImageCollectionPaths!), "Dialog cancellation modified collection.");
            Require(await browser.ApplyAsync([P("图4.png"), P("图10.png")]) && tile.Item.Path == P("图10.png"), "Removed current did not prefer following retained image.");
            Require(await browser.RelocateAsync(P("图2.png")) && tile.Item.ImageCollectionPaths!.SequenceEqual(new[] { P("图4.png"), P("图2.png") }), "Relocation failed to update current collection entry.");
            checks.Add("custom commit, copy isolation, reverse skip, transactional failed apply, dialog cancel, current removal and relocation");

            var workspacePath = P("navigation.mmp.json");
            await window.SaveWorkspaceAsync(workspacePath); await window.OpenWorkspaceAsync(workspacePath);
            await Until(() => window.Tiles[0].Session is { Ready: true, IsPlaying: false }, "Restored video did not finish entering paused state.");
            tile = window.Tiles[1]; browser = tile.Browser!;
            Require(tile.Item.Path == P("图2.png") && tile.Item.ImageCollectionPaths!.SequenceEqual(new[] { P("图4.png"), P("图2.png") }) && !window.Tiles[0].Session!.IsPlaying, "Workspace failed to restore collection/current/paused video.");
            Require(await browser.ApplyAsync(null) && tile.Item.ImageCollectionPaths == null && browser.Paths.Count == 5, "Return to directory browsing failed.");
            checks.Add("workspace v2 round trip, paused video restore, return to directory mode");

            Make("临时新增.png", 600, 300);
            await browser.RefreshAsync();
            Require(browser.Paths.Contains(P("临时新增.png")), "Directory refresh did not discover added file.");
            File.Delete(P("临时新增.png")); await browser.RefreshAsync();
            Require(!browser.Paths.Contains(P("临时新增.png")), "Directory refresh retained deleted noncurrent file.");
            await window.RemoveAsync(window.Tiles[^1]); await window.ImportAsync([P("图3.png")]);
            var broken = window.Tiles[^1];
            Require(broken.Error != null && await broken.Browser!.NavigateAsync(1) && broken.Error == null && broken.Item.Path == P("图4.png"), "Initially broken image could not recover through navigation.");
            checks.Add("directory addition/deletion refresh, initially broken view recovers by navigating");

            await browser.ApplyAsync([P("图1.png"), P("图2.png"), P("图4.png"), P("图10.png")]);
            var samples = new List<long>();
            var weakFrames = new List<WeakReference>();
            void ObserveFrame(ImageFrame frame, bool _) => weakFrames.Add(new WeakReference(frame.Bitmap));
            browser.Committed += ObserveFrame;
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = 0; i < 30; i++)
                {
                    int index = ImageSequence.IndexOf(browser.Paths, tile.Item.Path);
                    await browser.NavigateAsync(index == browser.Paths.Count - 1 ? -1 : 1);
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                samples.Add(Process.GetCurrentProcess().PrivateMemorySize64);
            }
            browser.Committed -= ObserveFrame;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int retainedFrames = weakFrames.Count(w => w.IsAlive);
            Require(retainedFrames <= 2, "Previous bitmaps remain referenced.");
            memory = new { switches = weakFrames.Count, retainedFrames, privateMemoryBytes = samples };
            checks.Add("120 sequential switches, previous bitmap collection and memory sampling");
            await window.RemoveAsync(window.Tiles[^1]);
            window.ToggleFullscreen(); window.ToggleFullscreen(); window.DiagnosticMode(LayoutMode.Automatic);
            await Task.Delay(500);
            checks.Add("view removal, fullscreen transition and explicit auto layout");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = true, checks, memory }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = false, checks, memory, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static async Task VerifyImageRequestOrderingAsync(List<string> checks, string first, string second, string third)
    {
        var loaded = ImageLoader.Load(first); var frame = new ImageFrame(loaded.Bitmap, loaded.Aspect);
        var calls = new List<(string Path, TaskCompletionSource<ImageFrame> Completion)>();
        var item = new MediaItem { Path = first, Kind = MediaKind.Image, ImageCollectionPaths = [first, second, third] };
        using var browser = new ImageBrowser(item, path =>
        {
            var completion = new TaskCompletionSource<ImageFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Add((path, completion)); return completion.Task;
        });
        var initial = browser.InitializeAsync(); await Until(() => calls.Count == 1, "Initial controlled decode missing.");
        calls[0].Completion.SetResult(frame); Require(await initial, "Controlled initialization failed.");
        var slow = browser.NavigateAsync(1); await Until(() => calls.Count == 2, "Slow decode missing.");
        var latest = browser.NavigateAsync(1);
        Require(calls.Count == 2 && item.Path == first, "Concurrent decode or premature state commit.");
        calls[1].Completion.SetResult(frame); await Until(() => calls.Count == 3, "Latest decode missing.");
        Require(!await slow && item.Path == first && calls[2].Path == third, "Stale decode committed.");
        calls[2].Completion.SetResult(frame); Require(await latest && item.Path == third, "Latest request failed to win.");
        using var cancel = new CancellationTokenSource();
        var editing = browser.ApplyAsync([second], cancel.Token); await Until(() => calls.Count == 4, "Editing decode missing.");
        cancel.Cancel(); calls[3].Completion.SetResult(frame);
        Require(!await editing && item.Path == third && item.ImageCollectionPaths!.Count == 3, "Cancelled apply committed.");
        var disposing = browser.NavigateAsync(-1); await Until(() => calls.Count == 5, "Disposal decode missing.");
        browser.Dispose(); calls[4].Completion.SetResult(frame);
        Require(!await disposing && item.Path == third, "Released view accepted a decode.");
        checks.Add("controlled decoder: single-flight, latest target wins, cancelled edit and released view reject stale results");
    }
}
