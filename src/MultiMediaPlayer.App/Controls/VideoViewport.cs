using System.Runtime.InteropServices;
using MultiMediaPlayer.Core;

namespace MultiMediaPlayer.App.Controls;

/// <summary>Fits VLC's child window into the VideoView HWND. The parent clips the oversized cover surface.</summary>
internal sealed class VideoViewport : IDisposable
{
    private const uint ChildWindow = 0x40000000;
    private const uint VisibleWindow = 0x10000000;
    private const uint ClipChildren = 0x02000000;
    private const uint NoActivateOrZOrder = 0x0014;
    private const int RenderLimit = 16_384;
    private readonly nint _parent;
    public nint Handle { get; private set; }

    public VideoViewport(nint parent)
    {
        _parent = parent;
        Handle = CreateWindowEx(0, "STATIC", null, ChildWindow | VisibleWindow | ClipChildren,
            0, 0, 1, 1, parent, 0, 0, 0);
        if (Handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public ViewportGeometry? Resize(MediaItem item)
    {
        if (Handle == 0 || !GetClientRect(_parent, out var rect) || rect.Right < 1 || rect.Bottom < 1) return null;
        var geometry = ViewportGeometry.Calculate(rect.Right, rect.Bottom, item, RenderLimit);
        var content = geometry.Content;
        // Keep the native surface over the entire viewport so VLC paints the letterbox bars.
        SetWindowPos(Handle, 0, (int)Math.Round(Math.Min(0, content.X)), (int)Math.Round(Math.Min(0, content.Y)),
            Math.Min(RenderLimit, Math.Max(rect.Right, (int)Math.Round(content.Width))),
            Math.Min(RenderLimit, Math.Max(rect.Bottom, (int)Math.Round(content.Height))), NoActivateOrZOrder);
        return geometry;
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        DestroyWindow(Handle);
        Handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint ex, string cls, string? name, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
}
