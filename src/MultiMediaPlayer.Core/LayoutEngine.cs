namespace MultiMediaPlayer.Core;

public record Divider(Guid NodeId, Box Bounds, SplitAxis Axis, double Ratio);
public record LayoutResult(LayoutNode? Root, Dictionary<Guid, Box> Tiles, List<Divider> Dividers, double ContentArea);
public interface ILayoutEngine
{
    LayoutResult Arrange(IReadOnlyList<MediaItem> items, double width, double height, LayoutMode mode,
        LayoutNode? previous = null, Guid? focused = null, CancellationToken cancellation = default);
}

/// <summary>Bounded candidate search. Every automatic result competes against the best uniform grid.</summary>
public sealed class LayoutEngine : ILayoutEngine
{
    public static TilePlacement DropPlacement(double x, double y)
    {
        var nearest = Math.Min(Math.Min(x, 1 - x), Math.Min(y, 1 - y));
        if (nearest > .25) return TilePlacement.Center;
        if (nearest == y) return TilePlacement.Above;
        if (nearest == 1 - y) return TilePlacement.Below;
        return nearest == x ? TilePlacement.Left : TilePlacement.Right;
    }

    public static LayoutResult Evaluate(LayoutNode? root, IReadOnlyList<MediaItem> items, double width, double height)
    {
        var tiles = new Dictionary<Guid, Box>();
        var dividers = new List<Divider>();
        void Visit(LayoutNode n, Box r)
        {
            if (n.ItemId is Guid id) { tiles[id] = r; return; }
            if (n.First == null || n.Second == null) return;
            dividers.Add(new(n.Id, r, n.Axis, n.Ratio));
            if (n.Axis == SplitAxis.Horizontal)
            {
                var w = r.Width * n.Ratio;
                Visit(n.First, r with { Width = w });
                Visit(n.Second, new(r.X + w, r.Y, r.Width - w, r.Height));
            }
            else
            {
                var h = r.Height * n.Ratio;
                Visit(n.First, r with { Height = h });
                Visit(n.Second, new(r.X, r.Y + h, r.Width, r.Height - h));
            }
        }
        if (root != null) Visit(root, new(0, 0, Math.Max(1, width), Math.Max(1, height)));
        double area = 0;
        foreach (var item in items)
        {
            if (!tiles.TryGetValue(item.Id, out var r)) continue;
            // Mirrors tile border, gutter and title chrome in the desktop view.
            var w = Math.Max(0, r.Width - 8); var h = Math.Max(0, r.Height - 34);
            var a = SafeAspect(item.DisplayAspect);
            area += item.Fit == FitMode.Cover ? w * h : Math.Min(w, h * a) * Math.Min(h, w / a);
        }
        return new(root, tiles, dividers, area);
    }

    public LayoutResult Arrange(IReadOnlyList<MediaItem> items, double width, double height, LayoutMode mode,
        LayoutNode? previous = null, Guid? focused = null, CancellationToken cancellation = default)
    {
        if (items.Count > 9) throw new ArgumentOutOfRangeException(nameof(items));
        if (items.Count == 0) return Evaluate(null, items, width, height);
        if (mode == LayoutMode.Custom && previous != null) return Evaluate(previous.Copy(), items, width, height);
        var best = Grid(items, width, height);
        if (mode == LayoutMode.Grid || items.Count == 1) return best;
        if (mode == LayoutMode.Focus)
        {
            var main = items.FirstOrDefault(i => i.Id == focused) ?? items[0];
            var rest = items.Where(i => i.Id != main.Id).ToArray();
            bool wide = width >= height;
            var secondary = Grid(rest, wide ? width * .4 : width, wide ? height : height * .4);
            return Evaluate(LayoutNode.Split(wide ? SplitAxis.Horizontal : SplitAxis.Vertical, .6,
                LayoutNode.Leaf(main.Id), secondary.Root!), items, width, height);
        }
        var previousRects = Evaluate(previous, items, width, height).Tiles;
        double Movement(LayoutResult r) => r.Tiles.Sum(t => previousRects.TryGetValue(t.Key, out var p)
            ? Math.Abs(p.X - t.Value.X) + Math.Abs(p.Y - t.Value.Y) : 0);
        double minArea = width * height / (items.Count * 2d);
        void Consider(LayoutNode root)
        {
            var c = Evaluate(root, items, width, height);
            if (c.Tiles.Count != items.Count || c.Tiles.Values.Any(r => r.Area < minArea - .01)) return;
            if (c.ContentArea > best.ContentArea + .01 ||
                (Math.Abs(c.ContentArea - best.ContentArea) <= .01 && Movement(c) < Movement(best))) best = c;
        }
        if (previous != null && previous.Walk().Where(n => n.IsLeaf).Select(n => n.ItemId!.Value).Order().SequenceEqual(items.Select(i => i.Id).Order()))
            Consider(previous.Copy());
        var sorted = items.OrderBy(i => SafeAspect(i.DisplayAspect)).ToArray();
        MediaItem[][] orders = [items.ToArray(), sorted, sorted.Reverse().ToArray(), sorted.Where((_, i) => i % 2 == 0).Concat(sorted.Where((_, i) => i % 2 != 0).Reverse()).ToArray()];
        foreach (var order in orders)
        {
            // All contiguous row/column groupings: at most 256 for nine items.
            for (int mask = 0; mask < 1 << (items.Count - 1); mask++)
            {
                cancellation.ThrowIfCancellationRequested();
                var groups = new List<MediaItem[]>();
                int start = 0;
                for (int j = 0; j < order.Length; j++)
                    if (j == order.Length - 1 || (mask & (1 << j)) != 0) { groups.Add(order[start..(j + 1)]); start = j + 1; }
                foreach (bool columns in new[] { false, true })
                {
                    Consider(Grouped(groups, columns, false));
                    Consider(Grouped(groups, columns, true));
                }
            }
            for (int seed = 0; seed < 8; seed++)
                Consider(Recursive(order, width, height, seed));
            // Let two adjacent regions choose different internal orientations. In a
            // wide viewport this can place two portraits beside a stack of landscapes.
            for (int cut = 1; cut < order.Length; cut++)
            {
                cancellation.ThrowIfCancellationRequested();
                var a = order[..cut]; var b = order[cut..];
                foreach (var axis in new[] { SplitAxis.Horizontal, SplitAxis.Vertical })
                foreach (var ratio in new[] { (double)cut / order.Length, .5, .4, .6 }.Distinct())
                {
                    var aw = axis == SplitAxis.Horizontal ? width * ratio : width;
                    var ah = axis == SplitAxis.Vertical ? height * ratio : height;
                    var bw = axis == SplitAxis.Horizontal ? width - aw : width;
                    var bh = axis == SplitAxis.Vertical ? height - ah : height;
                    LayoutNode[] Variants(MediaItem[] group, double w, double h) => group.Length == 1
                        ? [LayoutNode.Leaf(group[0].Id)]
                        : [Grid(group, w, h).Root!, Grouped([group], false, false), Grouped([group], true, false)];
                    var second = Variants(b, bw, bh);
                    foreach (var left in Variants(a, aw, ah))
                    foreach (var right in second)
                        Consider(LayoutNode.Split(axis, ratio, left, right));
                }
            }
        }
        return best;
    }

    private static LayoutResult Grid(IReadOnlyList<MediaItem> items, double w, double h)
    {
        if (items.Count == 0) return Evaluate(null, items, w, h);
        LayoutResult? best = null;
        for (int rows = 1; rows <= items.Count; rows++)
        {
            var groups = new List<MediaItem[]>(); int cursor = 0;
            for (int r = 0; r < rows; r++)
            {
                int count = items.Count / rows + (r < items.Count % rows ? 1 : 0);
                groups.Add(items.Skip(cursor).Take(count).ToArray()); cursor += count;
            }
            var nodes = groups.Select(g => Chain(g.Select(i => LayoutNode.Leaf(i.Id)).ToArray(), g.Select(_ => 1d).ToArray(), SplitAxis.Horizontal)).ToArray();
            var root = Chain(nodes, groups.Select(_ => 1d).ToArray(), SplitAxis.Vertical);
            var candidate = Evaluate(root, items, w, h);
            if (best == null || candidate.ContentArea > best.ContentArea) best = candidate;
        }
        return best!;
    }
    private static LayoutNode Grouped(List<MediaItem[]> groups, bool columns, bool equal)
    {
        double Aspect(MediaItem m) => columns ? 1 / SafeAspect(m.DisplayAspect) : SafeAspect(m.DisplayAspect);
        var nodes = groups.Select(g => Chain(g.Select(i => LayoutNode.Leaf(i.Id)).ToArray(),
            g.Select(Aspect).ToArray(), columns ? SplitAxis.Vertical : SplitAxis.Horizontal)).ToArray();
        var weights = groups.Select(g => equal ? 1d : 1 / g.Sum(Aspect)).ToArray();
        return Chain(nodes, weights, columns ? SplitAxis.Horizontal : SplitAxis.Vertical);
    }
    private static LayoutNode Chain(LayoutNode[] nodes, double[] weights, SplitAxis axis)
    {
        if (nodes.Length == 1) return nodes[0];
        return LayoutNode.Split(axis, weights[0] / weights.Sum(), nodes[0], Chain(nodes[1..], weights[1..], axis));
    }
    private static LayoutNode Recursive(MediaItem[] items, double w, double h, int seed)
    {
        if (items.Length == 1) return LayoutNode.Leaf(items[0].Id);
        int cut = seed % 2 == 0 ? items.Length / 2 : (items.Length + 1) / 2;
        bool horizontal = (seed & 2) == 0 ? w / h > Math.Sqrt(items.Average(i => SafeAspect(i.DisplayAspect))) : w >= h;
        double ratio = (seed & 4) == 0 ? (double)cut / items.Length : .5;
        return LayoutNode.Split(horizontal ? SplitAxis.Horizontal : SplitAxis.Vertical, ratio,
            Recursive(items[..cut], horizontal ? w * ratio : w, horizontal ? h : h * ratio, seed),
            Recursive(items[cut..], horizontal ? w * (1 - ratio) : w, horizontal ? h : h * (1 - ratio), seed));
    }
    public static double SafeAspect(double a) => double.IsFinite(a) && a > 0 ? a : 16d / 9;
}
