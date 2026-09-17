using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Xt1.Core.Imaging;

namespace Xt1.App.Rendering;

public enum ImageChannelMode
{
    Rgba,
    Red,
    Green,
    Blue,
    Alpha,
}

/// <summary>Pan/zoom RGBA image viewer with channel isolation and optional gamma display.</summary>
public sealed class Image2DViewer : Control
{
    private RgbaImage? _image;
    private Bitmap? _bitmap;
    private ImageChannelMode _channel = ImageChannelMode.Rgba;
    private bool _applyGamma;
    private bool _opaqueAlpha;
    private bool _checkerboard = true;

    public Image2DViewer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(32, 32, 36);
    }

    public float Zoom { get; private set; } = 1f;

    public PointF Pan { get; private set; }

    public bool ApplyGamma
    {
        get => _applyGamma;
        set
        {
            _applyGamma = value;
            RebuildBitmap();
            Invalidate();
        }
    }

    public bool OpaqueAlpha
    {
        get => _opaqueAlpha;
        set
        {
            _opaqueAlpha = value;
            RebuildBitmap();
            Invalidate();
        }
    }

    public ImageChannelMode Channel
    {
        get => _channel;
        set
        {
            _channel = value;
            RebuildBitmap();
            Invalidate();
        }
    }

    public void SetImage(RgbaImage? image) => SetImage(image, preserveView: false);

    /// <summary>
    /// Replace the image. With <paramref name="preserveView"/> the current pan/zoom is kept, which is
    /// what scrubbing a volume's slices wants; otherwise the new image is fitted to the window.
    /// </summary>
    public void SetImage(RgbaImage? image, bool preserveView)
    {
        bool fit = !preserveView || _image is null;
        _image = image;
        if (fit)
        {
            Zoom = 1f;
            Pan = PointF.Empty;
        }
        RebuildBitmap();
        if (fit) FitToWindow();
        Invalidate();
    }

    public RgbaImage? Image => _image;

    public void FitToWindow()
    {
        if (_image is null || _image.Width == 0 || _image.Height == 0)
            return;
        float zoomX = (float)ClientSize.Width / _image.Width;
        float zoomY = (float)ClientSize.Height / _image.Height;
        Zoom = Math.Max(0.01f, Math.Min(zoomX, zoomY) * 0.98f);
        Pan = new PointF(
            (ClientSize.Width - _image.Width * Zoom) / 2f,
            (ClientSize.Height - _image.Height * Zoom) / 2f);
        Invalidate();
    }

    public void ZoomBy(float factor, PointF anchor)
    {
        if (_image is null) return;
        float before = Zoom;
        Zoom = Math.Clamp(Zoom * factor, 0.02f, 64f);
        float scale = Zoom / before;
        Pan = new PointF(
            anchor.X - (anchor.X - Pan.X) * scale,
            anchor.Y - (anchor.Y - Pan.Y) * scale);
        Invalidate();
    }

    private void RebuildBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (_image is null) return;

        int w = _image.Width;
        int h = _image.Height;
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * h];
            var source = _image.Pixels;
            var gamma = _applyGamma ? Srgb.DisplayLut : null;

            for (int y = 0; y < h; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    int src = (y * w + x) * 4;
                    byte r = source[src + 0];
                    byte g = source[src + 1];
                    byte b = source[src + 2];
                    byte a = source[src + 3];

                    switch (_channel)
                    {
                        case ImageChannelMode.Red:
                            r = g = b = source[src + 0];
                            a = 255;
                            break;
                        case ImageChannelMode.Green:
                            r = g = b = source[src + 1];
                            a = 255;
                            break;
                        case ImageChannelMode.Blue:
                            r = g = b = source[src + 2];
                            a = 255;
                            break;
                        case ImageChannelMode.Alpha:
                            r = g = b = source[src + 3];
                            a = 255;
                            break;
                    }

                    if (gamma is not null)
                    {
                        r = gamma[r];
                        g = gamma[g];
                        b = gamma[b];
                    }
                    if (_opaqueAlpha && _channel == ImageChannelMode.Rgba)
                        a = 255;

                    int dst = row + x * 4;
                    buffer[dst + 0] = b;
                    buffer[dst + 1] = g;
                    buffer[dst + 2] = r;
                    buffer[dst + 3] = a;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        _bitmap = bitmap;
    }

    private Bitmap? _checkerTile;
    private TextureBrush? _checkerBrush;
    private Size _checkerBrushSize;

    /// <summary>
    /// Checkerboard brush, built once per client size. Redrawing thousands of GDI rectangles on
    /// every paint (including each pan mouse-move) is replaced by one tiled fill.
    /// </summary>
    private TextureBrush? GetCheckerBrush()
    {
        var size = ClientSize;
        if (_checkerBrush is not null && _checkerBrushSize == size) return _checkerBrush;

        _checkerBrush?.Dispose();
        _checkerBrush = null;
        _checkerTile?.Dispose();

        const int cell = 16;
        _checkerTile = new Bitmap(cell * 2, cell * 2);
        using (var g = Graphics.FromImage(_checkerTile))
        {
            using var light = new SolidBrush(Color.FromArgb(70, 70, 76));
            using var dark = new SolidBrush(Color.FromArgb(52, 52, 58));
            g.FillRectangle(light, 0, 0, cell * 2, cell * 2);
            g.FillRectangle(dark, 0, 0, cell, cell);
            g.FillRectangle(dark, cell, cell, cell, cell);
        }

        _checkerBrush = new TextureBrush(_checkerTile, WrapMode.Tile);
        _checkerBrushSize = size;
        return _checkerBrush;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checkerBrush?.Dispose();
            _checkerTile?.Dispose();
            _bitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_checkerboard && _bitmap is not null && _channel == ImageChannelMode.Rgba && !_opaqueAlpha)
        {
            var brush = GetCheckerBrush();
            if (brush is not null) g.FillRectangle(brush, ClientRectangle);
        }

        if (_bitmap is null)
        {
            using var font = new Font(FontFamily.GenericSansSerif, 10f);
            using var brush = new SolidBrush(Color.Gainsboro);
            const string message = "No preview";
            var size = g.MeasureString(message, font);
            g.DrawString(message, font, brush,
                (ClientSize.Width - size.Width) / 2, (ClientSize.Height - size.Height) / 2);
            return;
        }

        g.InterpolationMode = Zoom >= 2f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_bitmap, Pan.X, Pan.Y, _image!.Width * Zoom, _image.Height * Zoom);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(e.Delta > 0 ? 1.2f : 1f / 1.2f, new PointF(e.X, e.Y));
    }

    private bool _dragging;
    private Point _lastMouse;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _lastMouse = e.Location;
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Pan = new PointF(Pan.X + (e.X - _lastMouse.X), Pan.Y + (e.Y - _lastMouse.Y));
        _lastMouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        FitToWindow();
    }
}
