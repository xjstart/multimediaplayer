using System.Text.Json;
using MultiMediaPlayer.Core;

internal static class ImageSequenceTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "中文 图片");
        string P(string name) => Path.Combine(root, name + ".png");
        var ordered = new[] { "图10", "图2", "图1", "图0003", "图9999999999999999999999999" }.Select(P).Order(ImageSequence.NaturalOrder);
        check(ordered.SequenceEqual(new[] { "图1", "图2", "图0003", "图10", "图9999999999999999999999999" }.Select(P)), "Natural sort including big numbers failed.");
        check(ImageSequence.Distinct([P("A"), P("a"), Path.Combine(root, ".", "A.png")]).Count == 1, "Path normalization/deduplication failed.");
        var sequence = new[] { "1", "2", "3", "4", "5" }.Select(P).ToList();
        check(!ImageSequence.Navigate(sequence, sequence[0], -1).Any() && !ImageSequence.Navigate(sequence, sequence[^1], 1).Any(), "Navigation wrapped.");
        check(ImageSequence.Navigate(sequence, sequence[2], -1).SequenceEqual(sequence.Take(2).Reverse()), "Previous traversal failed.");
        check(ImageSequence.Navigate(sequence, sequence[1], 1).SequenceEqual(sequence.Skip(2)), "Next traversal failed.");
        check(ImageSequence.Move(sequence, sequence.Skip(1).Take(2), -1).SequenceEqual(new[] { sequence[1], sequence[2], sequence[0], sequence[3], sequence[4] }), "Multiselect upward order failed.");
        check(ImageSequence.Move(sequence, sequence.Skip(1).Take(2), 1).SequenceEqual(new[] { sequence[0], sequence[3], sequence[1], sequence[2], sequence[4] }), "Multiselect downward order failed.");
        check(ImageSequence.Move(sequence, sequence, -1).SequenceEqual(sequence), "Boundary movement changed order.");
        check(ImageSequence.ApplyCandidates(sequence, [sequence[0], sequence[3], sequence[4]], sequence[2]).SequenceEqual(new[] { sequence[3], sequence[4], sequence[0] }), "Removed current selection failed.");
        check(ImageSequence.ApplyCandidates(sequence, [sequence[0], sequence[1]], sequence[4]).SequenceEqual(new[] { sequence[1], sequence[0] }), "Previous retained selection failed.");
        check(ImageSequence.Append([sequence[3]], [sequence[2], sequence[1], sequence[3]]).SequenceEqual(new[] { sequence[3], sequence[1], sequence[2] }), "Append order or duplicates failed.");
        check(ImageSequence.Remove(sequence, [sequence[1], sequence[3]]).SequenceEqual(new[] { sequence[0], sequence[2], sequence[4] }), "Batch removal failed.");
        bool emptyRefused = false;
        try { ImageSequence.Remove(sequence, sequence); } catch (InvalidOperationException) { emptyRefused = true; }
        check(emptyRefused, "Last image removal accepted.");
        var item = new MediaItem { Path = sequence[0], Kind = MediaKind.Image, ImageCollectionPaths = sequence.ToList() };
        var copy = item.Copy(); copy.ImageCollectionPaths!.RemoveAt(0);
        check(item.ImageCollectionPaths.Count == 5, "Copy aliases original list.");

        var workspacePath = Path.Combine(Path.GetTempPath(), $"mmp-image-tests-{Guid.NewGuid():N}.json");
        try
        {
            var workspace = new Workspace { Items = [item] };
            await WorkspaceStore.SaveAsync(workspacePath, workspace);
            var restored = await WorkspaceStore.LoadAsync(workspacePath);
            check(restored.Version == 2 && restored.Items[0].ImageCollectionPaths!.SequenceEqual(sequence), "Image collection round trip failed.");
            await File.WriteAllTextAsync(workspacePath, """{"Version":1,"Items":[{"Path":"old.png","Kind":"Image"}]}""");
            check((await WorkspaceStore.LoadAsync(workspacePath)).Items[0].ImageCollectionPaths == null, "Legacy image should browse directory.");
            await File.WriteAllTextAsync(workspacePath, """{"Version":2,"Items":[{"Path":"./a.png","Kind":"Image","ImageCollectionPaths":["a.png","./A.png","b.png"]}]}""");
            var relative = (await WorkspaceStore.LoadAsync(workspacePath)).Items[0];
            check(relative.ImageCollectionPaths!.Count == 2 && relative.Path == Path.Combine(Path.GetTempPath(), "a.png"), "Relative paths/deduplication failed.");
            foreach (string json in new[] {
                """{"Items":[{"Path":"a.png","Kind":"Image","ImageCollectionPaths":[]}]}""",
                """{"Items":[{"Path":"a.png","Kind":"Image","ImageCollectionPaths":["b.png"]}]}""",
                """{"Items":[{"Path":"a.png","Kind":"Video","ImageCollectionPaths":["a.png"]}]}""",
                """{"Items":[{"Path":"a.png","Kind":"Image","ImageCollectionPaths":["a.png","https://example.com/x.png"]}]}""",
                """{"Items":[{"Path":"a.png","Kind":"Image","ImageCollectionPaths":["a.png","b.mp4"]}]}""" })
            {
                await File.WriteAllTextAsync(workspacePath, json);
                bool rejected = false;
                try { await WorkspaceStore.LoadAsync(workspacePath); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected = true; }
                check(rejected, "Invalid image collection accepted.");
            }
            await File.WriteAllTextAsync(workspacePath, "preserve-original");
            item.ImageCollectionPaths = Enumerable.Range(0, 10_000).Select(i => P($"{i}-{new string('a', 210)}")).ToList();
            item.Path = item.ImageCollectionPaths[0];
            bool tooLarge = false;
            try { await WorkspaceStore.SaveAsync(workspacePath, workspace); } catch (InvalidDataException) { tooLarge = true; }
            check(tooLarge && await File.ReadAllTextAsync(workspacePath) == "preserve-original", "Oversize save overwrote original.");
            await File.WriteAllBytesAsync(workspacePath, new byte[WorkspaceStore.MaximumBytes + 1]);
            bool readLimit = false;
            try { await WorkspaceStore.LoadAsync(workspacePath); } catch (InvalidDataException) { readLimit = true; }
            check(readLimit, "Oversize workspace read accepted.");
        }
        finally { File.Delete(workspacePath); }
    }
}
