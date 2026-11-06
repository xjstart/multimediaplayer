using System.Runtime.InteropServices;

namespace MultiMediaPlayer.App.Controls;

/// <summary>Fits VLC's child window into the VideoView HWND. The parent clips the oversized cover surface.</summary>
internal sealed class VideoViewport : IDisposable
{
    private const uint ChildWindow = 0x40000000;
    private const uint VisibleWindow = 0x10000000;
    private const uint ClipChildren = 0x02000000;
    private const uint NoActivateOrZOrder = 0x0014;
    private readonly nint _parent;
    public nint Handle { get; private set; }

    public VideoViewport(nint parent)
    {
        _parent = parent;
        Handle = CreateWindowEx(0, "STATIC", null, ChildWindow | VisibleWindow | ClipChildren,
            0, 0, 1, 1, parent, 0, 0, 0);
        if (Handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Resize(double aspect, bool cover)
    {
        if (Handle == 0 || !GetClientRect(_parent, out var rect) || rect.Right < 1 || rect.Bottom < 1) return;
        int width = rect.Right, height = rect.Bottom;
        if (cover && double.IsFinite(aspect) && aspect > 0)
        {
            double w = Math.Max(width, height * aspect), h = Math.Max(height, width / aspect);
            // D3D11 limits the render target; fall back to contain for pathological ratios.
            if (w <= 16_384 && h <= 16_384)
            {
                width = (int)Math.Ceiling(w);
                height = (int)Math.Ceiling(h);
            }
        }
        SetWindowPos(Handle, 0, (rect.Right - width) / 2, (rect.Bottom - height) / 2,
            width, height, NoActivateOrZOrder);
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
