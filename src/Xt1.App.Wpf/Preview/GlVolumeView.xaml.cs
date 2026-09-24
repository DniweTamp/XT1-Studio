using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xt1.Rendering.WinForms.Rendering;
using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using WinForms = System.Windows.Forms;

namespace Xt1.App.Wpf.Preview;

/// <summary>
/// Hosts the WinForms <see cref="GlPreviewControl"/> in the WPF tree through a
/// <see cref="System.Windows.Forms.Integration.WindowsFormsHost"/>. The renderer itself is the
/// shared renderer source compiled into Xt1.Rendering.WinForms, so the orbit camera, ray-marching
/// and shaders behave exactly as they do in the WinForms shell.
/// <para>
/// GL is best-effort: construction and every property push are guarded, and a failed context
/// leaves <see cref="IsReady"/> false with <see cref="Error"/> populated rather than throwing into
/// the window.
/// </para>
/// </summary>
public sealed partial class GlVolumeView : UserControl
{
    // Mirrors MainForm's preview-volume safety caps so a huge volume cannot exhaust memory.
    private const int MaxPreviewSlices = 512;
    private const long MaxPreviewVolumeTexels = 64L * 1024 * 1024;

    // The viewer border uses CornerRadius="10" and the tab margin insets this host by 6, so the same
    // shape is continued here by rounding the hosted Win32 window. A WindowsFormsHost child is a
    // native window that WPF cannot clip, so without this the OpenGL render paints square corners
    // over the card's rounded ones (the sky render is full-bleed, so it shows).
    private const int CornerRadius = 6;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    private readonly GlPreviewControl? _control;
    private string? _error;

    // The single composed volume fed to the GPU; slices are read back out of it on demand.
    private RgbaImage? _volume;

    private VolumeRenderMode _renderMode = VolumeRenderMode.Rgb;
    private bool _showSlices;
    private float _gamma = 1f;
    private float _exposure = 1f;
    private float _sliceZ = 0.5f;
    private bool _opaqueAlpha;
    private int _resolutionCap = 512;
    private ImageChannelMode _channel = ImageChannelMode.Rgba;

    public GlVolumeView()
    {
        InitializeComponent();
        try
        {
            _control = new GlPreviewControl { Dock = WinForms.DockStyle.Fill };
            _control.Load += OnControlLoad;
            // Keep the rounded region in step with the control's size.
            _control.SizeChanged += (_, _) => ApplyRoundedRegion();
            Host.Child = _control;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _control = null;
        }
    }

    /// <summary>Raised once the GL context has finished loading (or failed), so the shell can re-check readiness.</summary>
    public event Action? ReadyChanged;

    /// <summary>Raised when a freshly fed volume forced a render mode (uniform alpha needs RGB opacity).</summary>
    public event Action<VolumeRenderMode>? RenderModeSuggested;

    /// <summary>True once the GL context and shaders are up.</summary>
    public bool IsReady => _control is { Available: true };

    /// <summary>GL failure text, or null while initialisation is still pending.</summary>
    public string? Error => _error ?? _control?.LastError;

    /// <summary>True when a 3D/cube texture was decoded and handed to the viewport.</summary>
    public bool Has3DContent { get; private set; }

    public VolumeRenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            _renderMode = value;
            Push(c => c.RenderMode = value);
        }
    }

    public bool ShowSlices
    {
        get => _showSlices;
        set
        {
            _showSlices = value;
            Push(c => { c.ShowSlices = value; c.Invalidate(); });
        }
    }

    public float Gamma
    {
        get => _gamma;
        set
        {
            _gamma = value;
            Push(c => { c.Gamma = value; c.Invalidate(); });
        }
    }

    public float Exposure
    {
        get => _exposure;
        set
        {
            _exposure = value;
            Push(c => { c.Exposure = value; c.Invalidate(); });
        }
    }

    public float SliceZ
    {
        get => _sliceZ;
        set
        {
            _sliceZ = value;
            Push(c => { c.SliceZ = value; c.Invalidate(); });
        }
    }

    public bool OpaqueAlpha
    {
        get => _opaqueAlpha;
        set
        {
            _opaqueAlpha = value;
            Push(c => { c.OpaqueAlpha = value; c.Invalidate(); });
        }
    }

    /// <summary>Preview-only volume resolution cap; larger volumes are downscaled for the GPU.</summary>
    public int VolumeResolutionCap
    {
        get => _resolutionCap;
        set
        {
            _resolutionCap = value;
            Push(c => c.VolumeResolutionCap = value);
        }
    }

    /// <summary>Channel isolation, using the WPF viewer's enum and mapped onto the renderer's own.</summary>
    public ImageChannelMode Channel
    {
        get => _channel;
        set
        {
            _channel = value;
            Push(c => { c.Channel = value; c.Invalidate(); });
        }
    }

    /// <summary>Reset the orbit camera / skybox look, matching the WinForms "GL reset".</summary>
    public void ResetCamera() => Push(c => c.ResetCamera());

    /// <summary>
    /// Decode a texture the way the WinForms shell does and feed it to the viewport:
    /// volumes through <c>SetVolume</c> (respecting <see cref="VolumeResolutionCap"/>), cubemaps
    /// through all six faces, and everything else clears the view.
    /// </summary>
    public void SetTexture(EditableTexture? texture, int mip = 0)
    {
        if (_control is null)
        {
            Has3DContent = false;
            return;
        }

        try
        {
            if (texture is null)
            {
                Clear();
                return;
            }

            var type = texture.Header.Type;
            if (type.Is3D()) FeedVolume(_control, texture, mip);
            else if (type.IsCube()) FeedCube(_control, texture, mip);
            else Clear();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Has3DContent = false;
        }
    }

    /// <summary>Dimensions of the composed volume the viewport holds, or null when there is none.</summary>
    public (int Width, int Height, int Depth)? VolumeInfo =>
        _volume is { } volume ? (volume.Width, volume.Height, volume.Depth) : null;

    /// <summary>
    /// Copy one slice into a caller-owned buffer. Playback reads a slice per frame, so this avoids
    /// allocating a full multi-megabyte image every tick. Returns false when there is no volume or
    /// the destination does not match its size.
    /// </summary>
    public bool CopyVolumeSlice(int index, RgbaImage destination)
    {
        if (_volume is not { } volume || index < 0 || index >= volume.Depth) return false;
        if (destination.Depth != 1
            || destination.Width != volume.Width || destination.Height != volume.Height)
        {
            return false;
        }

        volume.Slice(index).CopyTo(destination.Pixels);
        destination.IsSrgb = volume.IsSrgb;
        return true;
    }

    /// <summary>
    /// Re-resolve the palette for the shell's current theme and repaint the viewport. The renderer
    /// uses the WinForms palette, a separate hand-kept copy of the WPF brushes, so the viewer role is
    /// taken from the shell's own resource afterwards: otherwise the light theme would keep a dark 3D
    /// backdrop next to the light 2D viewer.
    /// </summary>
    public void ApplyTheme()
    {
        try
        {
            Xt1.Rendering.WinForms.Theming.ThemeManager.Initialize(App.Settings.Theme);
            _control?.ApplyTheme(Xt1.Rendering.WinForms.Theming.ThemeManager.Palette);

            if (_control is not null && Application.Current?.TryFindResource("Brush.Viewer") is SolidColorBrush brush)
            {
                var color = brush.Color;
                _control.BackColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
            }
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
        }
    }

    private void Clear()
    {
        Has3DContent = false;
        _volume = null;
    }

    private void FeedVolume(GlPreviewControl control, EditableTexture texture, int mip)
    {
        var layout = texture.Layout;
        int sliceCount = layout.Applicable && mip >= 0 && mip < layout.Mips.Count
            ? Math.Max(1, layout.Mips[mip].SliceCount)
            : 1;

        int requested = PreviewSliceCount(texture, mip, sliceCount);
        var slices = TextureCodec.DecodeVolumeSlices(texture, mip, requested, out var error);
        if (slices.Count == 0)
        {
            Has3DContent = false;
            _volume = null;
            if (error is not null) _error = error;
            return;
        }

        // Mirror MainForm: a decoder may hand back the whole volume as one image, otherwise the
        // slices are composed into a single depth-N image for the GPU.
        RgbaImage volume;
        if (slices.Count == 1 && slices[0].Depth > 1)
        {
            volume = slices[0];
        }
        else
        {
            volume = new RgbaImage(slices[0].Width, slices[0].Height, slices.Count)
            {
                IsSrgb = slices[0].IsSrgb,
            };
            for (int z = 0; z < slices.Count; z++) volume.SetSlice(z, slices[z]);
        }

        _volume = volume;
        control.VolumeResolutionCap = _resolutionCap;
        control.SetVolume(volume);
        control.Mode = PreviewMode.Volume;
        Has3DContent = true;

        // A uniform alpha carries no density, so the alpha-blended view would be blank; derive the
        // opacity from RGB instead and let the shell move its render-mode combo to match.
        if (volume.IsAlphaUniform() && _renderMode != VolumeRenderMode.Mip)
        {
            _renderMode = VolumeRenderMode.Rgb;
            control.RenderMode = VolumeRenderMode.Rgb;
            control.Invalidate();
            RenderModeSuggested?.Invoke(VolumeRenderMode.Rgb);
        }
    }

    private void FeedCube(GlPreviewControl control, EditableTexture texture, int mip)
    {
        _volume = null;

        // HDR faces go to the GPU as floats so the exposure control acts on the real values.
        if (TextureCodec.IsHdr(texture.FormatInfo))
        {
            var hdr = TextureCodec.DecodeCubeFacesHdr(texture, mip, out var hdrError);
            if (hdr.Count == 6)
            {
                control.SetCubeFacesHdr(hdr);
                Has3DContent = true;
                return;
            }
            if (hdrError is not null) _error = hdrError;
        }

        var faces = TextureCodec.DecodeCubeFaces(texture, mip, out var error);
        if (faces.Count == 6)
        {
            control.SetCubeFaces(faces);
            Has3DContent = true;
            return;
        }

        Has3DContent = false;
        if (error is not null) _error = error;
    }

    /// <summary>Match MainForm: cap a giant preview volume by slices and total texels.</summary>
    private static int PreviewSliceCount(EditableTexture texture, int mip, int sliceCount)
    {
        var layout = texture.Layout;
        int width = layout.Applicable && mip < layout.Mips.Count ? layout.Mips[mip].Width : (int)texture.Header.Width;
        int height = layout.Applicable && mip < layout.Mips.Count ? layout.Mips[mip].Height : (int)texture.Header.Height;
        long perSlice = Math.Max(1L, (long)width * height);
        long byTexels = Math.Max(1L, MaxPreviewVolumeTexels / perSlice);
        return (int)Math.Max(1, Math.Min(sliceCount, Math.Min(MaxPreviewSlices, byTexels)));
    }

    private void OnControlLoad(object? sender, EventArgs e)
    {
        // GLControl.OnLoad raises Load before it builds the shaders, so readiness is only known
        // after this message returns; defer the notification to the dispatcher.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _control?.ApplyTheme(Xt1.Rendering.WinForms.Theming.ThemeManager.Palette);
            }
            catch
            {
                // Theming is cosmetic; a failure must not take the viewport down.
            }

            ApplyRoundedRegion();
            ReadyChanged?.Invoke();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Round the hosted window so the OpenGL render cannot paint over the viewer card's rounded
    /// corners. The region is in the hosted window's own (physical) pixels, so only the radius is
    /// scaled by the monitor DPI.
    /// </summary>
    private void ApplyRoundedRegion()
    {
        if (_control is null || !_control.IsHandleCreated) return;

        try
        {
            int width = _control.ClientSize.Width;
            int height = _control.ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            int diameter = (int)Math.Round(CornerRadius * 2 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            if (region != IntPtr.Zero) SetWindowRgn(_control.Handle, region, true);
        }
        catch
        {
            // A failure here only means square corners; never take the viewport down for it.
        }
    }

    /// <summary>Apply a property to the control, swallowing (and recording) any GL exception.</summary>
    private void Push(Action<GlPreviewControl> apply)
    {
        if (_control is null) return;
        try
        {
            apply(_control);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }


}


