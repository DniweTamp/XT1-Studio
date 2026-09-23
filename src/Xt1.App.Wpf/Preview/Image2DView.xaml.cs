using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xt1.App.Rendering;
using Xt1.Core.Imaging;

namespace Xt1.App.Wpf.Preview;

/// <summary>
/// Pan/zoom RGBA preview surface, a WPF port of <c>Xt1.App.Rendering.Image2DViewer</c>.
/// Owns its pixels (one reused BGRA buffer) and nothing else: the host supplies the viewer
/// background and the empty-state overlay.
/// </summary>
public sealed partial class Image2DView : UserControl
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 64.0;
    private const double FitMargin = 0.98;
    private const double WheelStep = 1.2;

    private RgbaImage? _image;
    private WriteableBitmap? _bitmap;

    // Single full-size staging buffer, reused for every process pass: pan/zoom never allocate and a
    // setting change never needs a second copy of the image.
    private byte[]? _buffer;

    private ImageChannelMode _channel = ImageChannelMode.Rgba;
    private bool _opaqueAlpha;
    private bool _showCheckerboard = true;

    private double _zoom = 1.0;
    private double _panX;
    private double _panY;

    // True once the user has zoomed, panned or picked 1:1: from then on a resize keeps their view.
    private bool _userAdjusted;

    private bool _dragging;
    private Point _lastDrag;

    // Set when SetImage/FitToWindow races layout (size still zero); the first arrange applies it.
    private bool _pendingFit;

    // Re-entrancy guard so a ViewChanged handler cannot make the event fire recursively.
    private bool _raisingViewChanged;

    public Image2DView()
    {
        InitializeComponent();
        ApplyTransforms();
        UpdateCheckerboard();
    }

    /// <summary>Raised after zoom, pan, fit, or image set/clear; never raised recursively.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>True while no usable image is displayed.</summary>
    public bool IsEmpty => _image is null;

    /// <summary>Current scale factor, for a host status readout.</summary>
    public double Zoom => _zoom;

    public ImageChannelMode Channel
    {
        get => _channel;
        set
        {
            if (_channel == value) return;
            _channel = value;
            Reprocess();
        }
    }

    public bool OpaqueAlpha
    {
        get => _opaqueAlpha;
        set
        {
            if (_opaqueAlpha == value) return;
            _opaqueAlpha = value;
            Reprocess();
        }
    }

    public bool ShowCheckerboard
    {
        get => _showCheckerboard;
        set
        {
            if (_showCheckerboard == value) return;
            _showCheckerboard = value;
            UpdateCheckerboard();
        }
    }

    public void SetImage(RgbaImage? image, bool preserveView = false)
    {
        bool fit = !preserveView || _image is null;
        _image = IsUsable(image) ? image : null;

        if (fit)
        {
            _zoom = 1.0;
            _panX = 0.0;
            _panY = 0.0;
            _userAdjusted = false;
        }

        Rebuild();
        if (fit) FitToWindowCore();
        ApplyTransforms();
        UpdateCheckerboard();
        RaiseViewChanged();
    }

    /// <summary>Fit to the viewport and stay fitted while the viewport is resized.</summary>
    public void FitToWindow()
    {
        _userAdjusted = false;
        if (FitToWindowCore()) RaiseViewChanged();
    }

    /// <summary>Zoom to 1:1 and centre the image in the viewport.</summary>
    public void ActualSize()
    {
        if (_image is null) return;

        _userAdjusted = true;
        _zoom = 1.0;
        _panX = (ActualWidth - _image.Width) / 2.0;
        _panY = (ActualHeight - _image.Height) / 2.0;
        ApplyTransforms();
        RaiseViewChanged();
    }

    private bool FitToWindowCore()
    {
        if (_image is null || _image.Width <= 0 || _image.Height <= 0) return false;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            _pendingFit = true;
            return false;
        }

        _pendingFit = false;
        double scaleX = w / _image.Width;
        double scaleY = h / _image.Height;
        _zoom = Math.Clamp(Math.Min(scaleX, scaleY) * FitMargin, MinZoom, MaxZoom);
        _panX = (w - _image.Width * _zoom) / 2.0;
        _panY = (h - _image.Height * _zoom) / 2.0;
        ApplyTransforms();
        return true;
    }

    private void ZoomBy(double factor, Point anchor)
    {
        if (_image is null) return;

        double before = _zoom;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (_zoom == before) return;

        _userAdjusted = true;

        // Keep the point under the cursor fixed: pan' = anchor - (anchor - pan) * scale.
        double scale = _zoom / before;
        _panX = anchor.X - (anchor.X - _panX) * scale;
        _panY = anchor.Y - (anchor.Y - _panY) * scale;
        ApplyTransforms();
        RaiseViewChanged();
    }

    private void Rebuild()
    {
        Surface.Source = null;

        if (_image is null)
        {
            _bitmap = null;
            _buffer = null;
            return;
        }

        int w = _image.Width;
        int h = _image.Height;

        // Reuse the staging buffer and bitmap while the dimensions are unchanged: stepping volume
        // slices or re-selecting a same-size texture would otherwise allocate 2 x w*h*4 bytes (8 MB
        // for a 1024x1024 mip) on every refresh.
        if (_buffer is null || _buffer.Length != w * h * 4
            || _bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _buffer = new byte[w * h * 4];
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }

        ProcessPixels();
        Surface.Source = _bitmap;
    }

    private void Reprocess()
    {
        if (_bitmap is not null) ProcessPixels();
        UpdateCheckerboard();
    }

    /// <summary>
    /// One pass over the source RGBA writing display BGRA. Channel isolation and the opaque-alpha
    /// override happen here, so a setting change only re-runs this loop. Values are shown exactly as
    /// stored: no display curve is applied, matching the DDS export and NVTT.
    /// </summary>
    private void ProcessPixels()
    {
        if (_image is null || _bitmap is null || _buffer is null) return;

        int w = _image.Width;
        int h = _image.Height;
        if (_buffer.Length != w * h * 4) return;

        byte[] src = _image.Pixels;
        byte[] dst = _buffer;
        ImageChannelMode channel = _channel;
        bool opaque = _opaqueAlpha && channel == ImageChannelMode.Rgba;

        int count = w * h;
        for (int i = 0; i < count; i++)
        {
            int p = i * 4;
            byte r = src[p + 0];
            byte g = src[p + 1];
            byte b = src[p + 2];
            byte a = src[p + 3];

            switch (channel)
            {
                case ImageChannelMode.Red:
                    r = g = b = src[p + 0];
                    a = 255;
                    break;
                case ImageChannelMode.Green:
                    r = g = b = src[p + 1];
                    a = 255;
                    break;
                case ImageChannelMode.Blue:
                    r = g = b = src[p + 2];
                    a = 255;
                    break;
                case ImageChannelMode.Alpha:
                    r = g = b = src[p + 3];
                    a = 255;
                    break;
            }

            if (opaque) a = 255;

            // RGBA source -> Bgra32 output.
            dst[p + 0] = b;
            dst[p + 1] = g;
            dst[p + 2] = r;
            dst[p + 3] = a;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), dst, w * 4, 0);
    }

    private void ApplyTransforms()
    {
        if (_image is not null)
        {
            // Layout size follows the drawn size, so the element never overflows its viewport and no
            // ancestor clip can cut the texture; the pan is the element's margin.
            Surface.Width = _image.Width * _zoom;
            Surface.Height = _image.Height * _zoom;
            Surface.Margin = new Thickness(_panX, _panY, 0, 0);
        }

        RenderOptions.SetBitmapScalingMode(Surface,
            _zoom >= 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void UpdateCheckerboard()
    {
        // The WinForms viewer painted the checkerboard only for RGBA with alpha left intact.
        bool show = _showCheckerboard && _bitmap is not null
                    && _channel == ImageChannelMode.Rgba && !_opaqueAlpha;
        Checker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsUsable(RgbaImage? image)
    {
        if (image is null) return false;
        int w = image.Width;
        int h = image.Height;
        if (w <= 0 || h <= 0) return false;
        // Volumes (Depth > 1) have a longer buffer; the 2D view only accepts a single slice.
        return image.Pixels.Length == (long)w * h * 4;
    }

    private void RaiseViewChanged()
    {
        if (_raisingViewChanged) return;
        _raisingViewChanged = true;
        try
        {
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _raisingViewChanged = false;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsEmpty) return;

        // Keep a fitted view fitted. Without this a late layout pass (or any resize) would leave a
        // stale scale/offset from the previous viewport size, which is how a mip-0 preview could end
        // up drawn far larger than its viewport and clipped to a quarter of the texture.
        if (_pendingFit || !_userAdjusted) FitToWindow();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_image is null) return;
        ZoomBy(e.Delta > 0 ? WheelStep : 1.0 / WheelStep, e.GetPosition(this));
        e.Handled = true;   // keep an enclosing ScrollViewer from scrolling
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image is null) return;

        // Double-click fits; check it before starting a drag so the mode is deterministic.
        if (e.ClickCount == 2)
        {
            EndDrag();
            FitToWindow();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _lastDrag = e.GetPosition(this);
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _image is null) return;

        Point p = e.GetPosition(this);
        _panX += p.X - _lastDrag.X;
        _panY += p.Y - _lastDrag.Y;
        _lastDrag = p;
        _userAdjusted = true;
        ApplyTransforms();
        RaiseViewChanged();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        Cursor = Cursors.Arrow;
    }

    private void EndDrag()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }
}

