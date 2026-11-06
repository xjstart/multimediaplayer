namespace MultiMediaPlayer.Core;

public readonly record struct ViewportGeometry(Box Content, double Zoom, double OverflowX, double OverflowY)
{
    public const double MinimumZoom = 1;
    public const double MaximumZoom = 4;

    public static double ClampZoom(double zoom) =>
        double.IsFinite(zoom) ? Math.Clamp(zoom, MinimumZoom, MaximumZoom) : MinimumZoom;

    public static double ClampPan(double offset) => double.IsFinite(offset) ? Math.Clamp(offset, -1, 1) : 0;

    public static ViewportGeometry Calculate(double width, double height, MediaItem item, double renderLimit = double.PositiveInfinity)
    {
        var aspect = LayoutEngine.SafeAspect(item.DisplayAspect);
        var containWidth = Math.Min(width, height * aspect);
        var containHeight = Math.Min(height, width / aspect);
        var fittedWidth = containWidth;
        var fittedHeight = containHeight;
        if (item.Fit == FitMode.Cover)
        {
            var coverWidth = Math.Max(width, height * aspect);
            var coverHeight = Math.Max(height, width / aspect);
            if (double.IsFinite(coverWidth) && double.IsFinite(coverHeight) &&
                coverWidth <= renderLimit && coverHeight <= renderLimit)
            {
                fittedWidth = coverWidth;
                fittedHeight = coverHeight;
            }
        }

        var zoom = Math.Min(ClampZoom(item.Zoom), renderLimit / Math.Max(fittedWidth, fittedHeight));
        var scaledWidth = fittedWidth * zoom;
        var scaledHeight = fittedHeight * zoom;
        var overflowX = Math.Max(0, (scaledWidth - width) / 2);
        var overflowY = Math.Max(0, (scaledHeight - height) / 2);
        return new(new((width - scaledWidth) / 2 + overflowX * ClampPan(item.PanX),
                       (height - scaledHeight) / 2 + overflowY * ClampPan(item.PanY),
                       scaledWidth, scaledHeight), zoom, overflowX, overflowY);
    }

    public (double X, double Y) Pan(double x, double y, double dx, double dy) =>
        (OverflowX > 0 ? ClampPan(x + dx / OverflowX) : x,
         OverflowY > 0 ? ClampPan(y + dy / OverflowY) : y);
}
