namespace MultiMediaPlayer.Core;

public enum MediaKind { Video, Image }
public enum FitMode { Contain, Cover }
public enum LayoutMode { Automatic, Grid, Focus, Custom }
public enum SplitAxis { Horizontal, Vertical } // Horizontal = side by side; Vertical = stacked.
public enum TilePlacement { Center, Above, Below, Left, Right }

public sealed class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public double DisplayAspect { get; set; } = 16d / 9;
    public FitMode Fit { get; set; }
    public bool Loop { get; set; } = true;
    public float Rate { get; set; } = 1;
    public long PositionMs { get; set; }
    public bool HardwareDecoding { get; set; } = true;
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public List<string>? ImageCollectionPaths { get; set; }
    public MediaItem Copy()
    {
        var copy = (MediaItem)MemberwiseClone();
        copy.ImageCollectionPaths = ImageCollectionPaths?.ToList();
        return copy;
    }
}

public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

public sealed class LayoutNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public SplitAxis Axis { get; set; }
    public double Ratio { get; set; } = .5;
    public LayoutNode? First { get; set; }
    public LayoutNode? Second { get; set; }
    public bool IsLeaf => ItemId.HasValue;
    public static LayoutNode Leaf(Guid id) => new() { ItemId = id };
    public static LayoutNode Split(SplitAxis axis, double ratio, LayoutNode a, LayoutNode b) =>
        new() { Axis = axis, Ratio = ratio, First = a, Second = b };
    public LayoutNode Copy() => new() { Id = Id, ItemId = ItemId, Axis = Axis, Ratio = Ratio, First = First?.Copy(), Second = Second?.Copy() };
    public IEnumerable<LayoutNode> Walk()
    {
        yield return this;
        if (First != null) foreach (var n in First.Walk()) yield return n;
        if (Second != null) foreach (var n in Second.Walk()) yield return n;
    }
    public LayoutNode? ParentOf(Guid item) => Walk().FirstOrDefault(n => n.First?.ItemId == item || n.Second?.ItemId == item);
    public static LayoutNode? Remove(LayoutNode? node, Guid item)
    {
        if (node == null || node.ItemId == item) return null;
        if (node.IsLeaf) return node;
        node.First = Remove(node.First, item);
        node.Second = Remove(node.Second, item);
        return node.First == null ? node.Second : node.Second == null ? node.First : node;
    }
    public void Swap(Guid a, Guid b)
    {
        foreach (var n in Walk())
            if (n.ItemId == a) n.ItemId = b; else if (n.ItemId == b) n.ItemId = a;
    }
    public LayoutNode? Reposition(Guid source, Guid target, TilePlacement placement)
    {
        if (source == target || !Enum.IsDefined(placement) ||
            !Walk().Any(n => n.ItemId == source) || !Walk().Any(n => n.ItemId == target))
            return null;

        var root = Copy();
        if (placement == TilePlacement.Center)
        {
            root.Swap(source, target);
            return root;
        }

        root = Remove(root, source)!;
        var axis = placement is TilePlacement.Left or TilePlacement.Right ? SplitAxis.Horizontal : SplitAxis.Vertical;
        root.Insert(target, source, axis, placement is TilePlacement.Left or TilePlacement.Above);
        return root;
    }
    public void Insert(Guid target, Guid added, SplitAxis axis, bool before = false)
    {
        var n = Walk().First(x => x.ItemId == target);
        n.ItemId = null; n.Axis = axis; n.Ratio = .5;
        n.First = Leaf(before ? added : target); n.Second = Leaf(before ? target : added);
    }
}

public sealed class Workspace
{
    public int Version { get; set; } = 2;
    public List<MediaItem> Items { get; set; } = [];
    public LayoutMode Mode { get; set; }
    public LayoutNode? Layout { get; set; }
    public Guid? SelectedId { get; set; }
    public Guid? FocusItemId { get; set; }
    public int Volume { get; set; } = 60;
    public bool Muted { get; set; }
}

public static class MediaFiles
{
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
    public static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".flv", ".ogv", ".3gp" };
    public static bool Supported(string path) => Images.Contains(System.IO.Path.GetExtension(path)) || Videos.Contains(System.IO.Path.GetExtension(path));
    public static MediaKind Kind(string path) => Images.Contains(System.IO.Path.GetExtension(path)) ? MediaKind.Image : MediaKind.Video;
}
