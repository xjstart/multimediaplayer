using System.Diagnostics;
using System.Text.Json;
using MultiMediaPlayer.Core;

int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
var engine = new LayoutEngine();
var random = new Random(20261106); var times = new List<double>();
foreach (var (width, height) in new[] { (1920d, 1080d), (1080d, 1920d), (3440d, 1440d), (850d, 420d) })
for (int count = 1; count <= 9; count++)
for (int trial = 0; trial < 5; trial++)
{
    var aspects = new[] { 16d/9, 9d/16, 1d, 2.4, .25, 4d, 4d/3 };
    var items = Enumerable.Range(0, count).Select(_ => new MediaItem { Path = "C:\\sample.mp4", DisplayAspect = aspects[random.Next(aspects.Length)] }).ToArray();
    var grid = engine.Arrange(items, width, height, LayoutMode.Grid);
    var timer = Stopwatch.StartNew(); var layout = engine.Arrange(items, width, height, LayoutMode.Automatic); times.Add(timer.Elapsed.TotalMilliseconds);
    Check(layout.ContentArea + .01 >= grid.ContentArea, "Automatic layout underperforms grid.");
    Check(layout.Tiles.Count == count, "Missing tile.");
    Check(Math.Abs(layout.Tiles.Values.Sum(r => r.Area) - width * height) < .01, "Partition does not fill viewport.");
    foreach (var (id, r) in layout.Tiles)
    {
        Check(r.X >= -.01 && r.Y >= -.01 && r.X + r.Width <= width + .01 && r.Y + r.Height <= height + .01, "Out-of-bounds tile.");
        Check(r.Area + .01 >= width * height / (2 * count), "Automatic tile too small.");
        foreach (var (otherId, other) in layout.Tiles.Where(p => p.Key != id))
            Check(Math.Min(r.X + r.Width, other.X + other.Width) - Math.Max(r.X, other.X) <= .01 || Math.Min(r.Y + r.Height, other.Y + other.Height) - Math.Max(r.Y, other.Y) <= .01, "Overlapping tiles.");
    }
    var stable = engine.Arrange(items, width, height, LayoutMode.Automatic, layout.Root);
    Check(layout.Tiles.All(p => stable.Tiles[p.Key] == p.Value), "Same inputs produce an unstable layout.");
}
var pair = new[] { new MediaItem { Path = "C:\\横屏.mp4" }, new MediaItem { Path = "C:\\竖屏.png", Kind = MediaKind.Image } };
var mixed = new[] { 16d / 9, 9d / 16, 1d, 9d / 16 }.Select(aspect => new MediaItem { DisplayAspect = aspect }).ToArray();
var mixedGrid = engine.Arrange(mixed, 1230, 510, LayoutMode.Grid);
var mixedAuto = engine.Arrange(mixed, 1230, 510, LayoutMode.Automatic);
Check(mixedAuto.ContentArea > mixedGrid.ContentArea * 1.08, "Mixed orientation layout should materially improve content utilization.");
var root = LayoutNode.Split(SplitAxis.Horizontal, .6, LayoutNode.Leaf(pair[0].Id), LayoutNode.Leaf(pair[1].Id));
var resized = engine.Arrange(pair, 2000, 1000, LayoutMode.Custom, root);
Check(resized.Tiles[pair[0].Id].Width == 1200, "Manual ratio lost.");
root.Swap(pair[0].Id, pair[1].Id);
Check(LayoutEngine.Evaluate(root, pair, 2000, 1000).Tiles[pair[1].Id].Width == 1200, "Swap changed region geometry.");
var third = Guid.NewGuid(); root.Insert(pair[0].Id, third, SplitAxis.Vertical);
Check(root.Walk().Count(n => n.IsLeaf) == 3, "Insert failed.");
root = LayoutNode.Remove(root, third)!;
Check(root.Walk().Count(n => n.IsLeaf) == 2, "Remove failed to collapse branch.");

foreach (var (placement, axis, sourceFirst) in new[]
{
    (TilePlacement.Above, SplitAxis.Vertical, true), (TilePlacement.Below, SplitAxis.Vertical, false),
    (TilePlacement.Left, SplitAxis.Horizontal, true), (TilePlacement.Right, SplitAxis.Horizontal, false)
})
{
    var moved = root.Reposition(pair[0].Id, pair[1].Id, placement)!;
    Check(moved.Axis == axis && moved.Ratio == .5 && (sourceFirst ? moved.First : moved.Second)?.ItemId == pair[0].Id,
        "Directional move failed after the root collapsed.");
}
var screenshot = LayoutNode.Split(SplitAxis.Horizontal, .4,
    LayoutNode.Split(SplitAxis.Horizontal, .5, LayoutNode.Leaf(pair[0].Id), LayoutNode.Leaf(pair[1].Id)),
    LayoutNode.Leaf(third));
var stacked = screenshot.Reposition(pair[1].Id, third, TilePlacement.Above)!;
Check(stacked.Ratio == .4 && stacked.First!.ItemId == pair[0].Id && stacked.Second!.Axis == SplitAxis.Vertical &&
    stacked.Second.First!.ItemId == pair[1].Id && stacked.Second.Second!.ItemId == third,
    "Moving across branches failed to stack two tiles beside the third.");
Check(screenshot.First!.Second!.ItemId == pair[1].Id && screenshot.Second!.ItemId == third &&
    screenshot.Reposition(third, third, TilePlacement.Left) == null &&
    screenshot.Reposition(third, Guid.Empty, TilePlacement.Left) == null, "Invalid move or input mutation.");
var exchanged = screenshot.Reposition(pair[1].Id, third, TilePlacement.Center)!;
Check(exchanged.First!.Second!.ItemId == third && exchanged.Second!.ItemId == pair[1].Id &&
    exchanged.Ratio == screenshot.Ratio, "Center drop should exchange leaves in place.");
Check(LayoutEngine.DropPlacement(.24, .1) == TilePlacement.Above &&
    LayoutEngine.DropPlacement(.24, .3) == TilePlacement.Left &&
    LayoutEngine.DropPlacement(.9, .84) == TilePlacement.Right &&
    LayoutEngine.DropPlacement(.72, .75) == TilePlacement.Below &&
    LayoutEngine.DropPlacement(.26, .74) == TilePlacement.Center, "Drop hit testing failed.");

Check(PlaybackPosition.Offset(2_000, -5_000, 10_000) == 0 &&
    PlaybackPosition.Offset(8_000, 5_000, 10_000) == 9_999 &&
    PlaybackPosition.Offset(2_000, 5_000, 10_000) == 7_000 &&
    PlaybackPosition.Offset(2_000, 30_000, 0) == 0 &&
    PlaybackPosition.Offset(2_000, long.MaxValue, long.MaxValue) == long.MaxValue - 1, "Relative seek bounds failed.");

var view = new MediaItem { DisplayAspect = 2, Zoom = 1.5, PanX = 1, PanY = -1 };
var contain = ViewportGeometry.Calculate(400, 400, view);
Check(contain.Content == new Box(0, 50, 600, 300) && contain.Pan(1, -1, -100, 50) == (0d, -1d),
    "Contain zoom, pan or letterboxing failed.");
view.Fit = FitMode.Cover;
var cover = ViewportGeometry.Calculate(400, 400, view);
Check(cover.Content == new Box(0, -200, 1200, 600) && cover.Pan(1, -1, 900, -900) == (1d, -1d),
    "Cover zoom or pan bounds failed.");
var capped = ViewportGeometry.Calculate(400, 400, view, 1000);
Check(capped.Zoom == 1.25 && capped.Content.Width == 1000, "Native size limit should cap the effective zoom.");
view.DisplayAspect = 1000;
var fallback = ViewportGeometry.Calculate(400, 400, view, 1000);
Check(fallback.Content.Width == 600 && fallback.OverflowY == 0, "Oversized cover should fall back to contain.");

var path = Path.Combine(Path.GetTempPath(), $"mmp-tests-{Guid.NewGuid():N}.json");
try
{
    var w = new Workspace { Items = pair.ToList(), Layout = root, Mode = LayoutMode.Custom, SelectedId = pair[1].Id, Volume = 42, Muted = true };
    pair[0].Zoom = 1.75; pair[0].PanX = -.5; pair[0].PanY = .25;
    pair[1].Zoom = 7; pair[1].PanX = -2; pair[1].PanY = double.NaN;
    await WorkspaceStore.SaveAsync(path, w);
    var restored = await WorkspaceStore.LoadAsync(path);
    Check(restored.Layout!.Ratio == .6 && restored.Items[0].Path == pair[0].Path && restored.Volume == 42 && restored.Muted, "Workspace round trip failed.");
    Check(restored.Items[0].Zoom == 1.75 && restored.Items[0].PanX == -.5 && restored.Items[0].PanY == .25 &&
        restored.Items[1].Zoom == 4 && restored.Items[1].PanX == -1 && restored.Items[1].PanY == 0,
        "Workspace zoom round trip or sanitization failed.");
    await File.WriteAllTextAsync(path, """{"Version":1,"Items":[{"Path":"old.mp4"}]}""");
    var legacy = (await WorkspaceStore.LoadAsync(path)).Items.Single();
    Check(legacy.Zoom == 1 && legacy.PanX == 0 && legacy.PanY == 0, "Legacy workspace should start at centered 100%.");
    foreach (var json in new[] { "{\"Version\":3}", "{\"Items\":null}", "{\"Mode\":\"Custom\",\"Items\":[{\"Path\":\"C:\\\\a.mp4\"}]}", "{\"Items\":[{\"Path\":\"https://example.com/x.mp4\"}]}" })
    {
        await File.WriteAllTextAsync(path, json);
        bool rejected = false; try { await WorkspaceStore.LoadAsync(path); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected = true; }
        Check(rejected, "Invalid workspace accepted.");
    }
    w.Layout.Ratio = double.NaN; bool invalidRatio = false;
    try { WorkspaceStore.Validate(w); } catch (InvalidDataException) { invalidRatio = true; }
    Check(invalidRatio, "Nonfinite ratio accepted.");
}
finally { File.Delete(path); }
await ImageSequenceTests.RunAsync(Check);
Console.WriteLine($"PASS: {assertions} assertions; 180 layout scenarios; median {times.Order().ElementAt(times.Count / 2):0.0} ms; max {times.Max():0.0} ms.");
