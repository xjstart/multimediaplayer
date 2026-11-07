using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiMediaPlayer.Core;

public static class WorkspaceStore
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, MaxDepth = 32 };
    public static async Task SaveAsync(string path, Workspace workspace)
    {
        Validate(workspace);
        workspace.Version = 2;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("工作区超过 2 MiB，请减少图片集条目后再保存。");
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes);
        File.Move(temporary, path, true);
    }
    public static async Task<Workspace> LoadAsync(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("工作区文件过大。");
        var w = JsonSerializer.Deserialize<Workspace>(await File.ReadAllTextAsync(path), Options) ?? throw new InvalidDataException("工作区为空。");
        Validate(w, Path.GetDirectoryName(Path.GetFullPath(path))!);
        return w;
    }
    public static void Validate(Workspace w, string? baseDirectory = null)
    {
        if (w.Version is not (1 or 2)) throw new InvalidDataException("不支持此工作区版本。");
        if (w.Items == null || w.Items.Count > 9 || w.Items.Any(i => i == null)) throw new InvalidDataException("工作区最多支持九个画面。");
        var ids = w.Items.Select(i => i.Id).ToHashSet();
        if (ids.Count != w.Items.Count || ids.Contains(Guid.Empty)) throw new InvalidDataException("媒体标识重复或无效。");
        foreach (var i in w.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Path) || Uri.TryCreate(i.Path, UriKind.Absolute, out var uri) && !uri.IsFile)
                throw new InvalidDataException("只支持本地媒体文件。");
            if (!Enum.IsDefined(i.Kind) || !Enum.IsDefined(i.Fit)) throw new InvalidDataException("媒体设置无效。");
            i.Path = ImageSequence.Normalize(i.Path, baseDirectory);
            if (i.ImageCollectionPaths is { } images)
            {
                images = ImageSequence.Distinct(images, baseDirectory);
                if (i.Kind != MediaKind.Image || images.Count == 0 ||
                    images.Any(p => string.IsNullOrWhiteSpace(p) || !MediaFiles.Images.Contains(Path.GetExtension(p)) ||
                        Uri.TryCreate(p, UriKind.Absolute, out var imageUri) && !imageUri.IsFile) ||
                    !images.Contains(i.Path, ImageSequence.PathComparer))
                    throw new InvalidDataException("图片集必须包含当前图片，且只能包含本地支持的图片。");
                i.ImageCollectionPaths = images.Distinct(ImageSequence.PathComparer).ToList();
            }
            i.DisplayAspect = LayoutEngine.SafeAspect(i.DisplayAspect);
            i.Zoom = ViewportGeometry.ClampZoom(i.Zoom);
            i.PanX = ViewportGeometry.ClampPan(i.PanX);
            i.PanY = ViewportGeometry.ClampPan(i.PanY);
            i.Rate = float.IsFinite(i.Rate) ? Math.Clamp(i.Rate, .25f, 4f) : 1;
            i.PositionMs = Math.Max(0, i.PositionMs);
        }
        if (!Enum.IsDefined(w.Mode)) throw new InvalidDataException("布局模式无效。");
        w.Volume = Math.Clamp(w.Volume, 0, 100);
        if (!ids.Contains(w.SelectedId ?? Guid.Empty)) w.SelectedId = w.Items.FirstOrDefault()?.Id;
        if (!ids.Contains(w.FocusItemId ?? Guid.Empty)) w.FocusItemId = w.SelectedId;
        if (w.Layout == null) { if (w.Mode == LayoutMode.Custom && w.Items.Count > 0) throw new InvalidDataException("自定义布局缺失。"); return; }
        var leaves = new HashSet<Guid>(); var nodes = new HashSet<Guid>();
        void Check(LayoutNode n, int depth)
        {
            if (depth > 16 || !nodes.Add(n.Id)) throw new InvalidDataException("布局树无效。");
            if (n.ItemId is Guid id)
            {
                if (!ids.Contains(id) || !leaves.Add(id) || n.First != null || n.Second != null) throw new InvalidDataException("布局引用无效。");
            }
            else
            {
                if (n.First == null || n.Second == null || !double.IsFinite(n.Ratio) || n.Ratio <= 0 || n.Ratio >= 1 || !Enum.IsDefined(n.Axis))
                    throw new InvalidDataException("分区比例无效。");
                Check(n.First, depth + 1); Check(n.Second, depth + 1);
            }
        }
        Check(w.Layout, 0);
        if (!ids.SetEquals(leaves)) throw new InvalidDataException("布局与媒体数量不符。");
    }
}
