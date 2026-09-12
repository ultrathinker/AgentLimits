using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentLimits;

/// <summary>
/// A translucent "ghost" window: renders a given UIElement to a bitmap and
/// follows the cursor during drag-drop. Doesn't intercept input (IsHitTestVisible=false).
/// </summary>
public sealed class DragGhost : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>Take a visual snapshot of the element and wrap it in a DragGhost.</summary>
    public static DragGhost? Create(DependencyObject? source)
    {
        if (source is not FrameworkElement el || el.ActualWidth <= 0 || el.ActualHeight <= 0) return null;

        var w = (int)Math.Ceiling(el.ActualWidth);
        var h = (int)Math.Ceiling(el.ActualHeight);

        // RenderTargetBitmap.Render(el) draws the element accounting for its actual
        // position in the visual tree: for the second block and beyond this leaves an
        // empty "tail" above it and offsets the ghost from the cursor. VisualBrush with
        // an explicit Rect gives a clean copy of the content starting at (0,0).
        var brush = new VisualBrush(el)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        return new DragGhost
        {
            Content = new System.Windows.Controls.Image
            {
                Source = bmp,
                Opacity = 0.55,
                Stretch = Stretch.None,
                SnapsToDevicePixels = true
            },
            Width = w,
            Height = h,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            Focusable = false,
            ShowActivated = false,
            // Position is set manually in FollowCursor; hide the initial placement.
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };
    }

    private DragGhost() { }

    /// <summary>Move the ghost to the current cursor position. Call from DragOver.</summary>
    public void FollowCursor()
    {
        if (!GetCursorPos(out var p)) return;

        // GetCursorPos returns coordinates in PHYSICAL screen pixels.
        // Window.Left/Top are in DIP. On displays scaled away from 100% (HiDPI),
        // without the conversion the ghost flies off to a corner of the screen.
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is { } ct)
        {
            var dip = ct.TransformFromDevice.Transform(new System.Windows.Point(p.X, p.Y));
            // A small offset so the cursor doesn't cover the image.
            Left = dip.X + 12;
            Top  = dip.Y + 12;
        }
        else
        {
            Left = p.X + 12;
            Top  = p.Y + 12;
        }
    }
}
