using System.Runtime.InteropServices;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using Xt1.Rendering.WinForms.Theming;
using Xt1.Core.Imaging;
using Xt1.Core.Rendering;

namespace Xt1.Rendering.WinForms.Rendering;

public enum PreviewMode
{
    Cube,
    Volume,
}

public enum VolumeRenderMode
{
    /// <summary>
    /// Maximum-intensity projection: each ray keeps its brightest sample. Ignores alpha entirely, so
    /// it always shows something regardless of the stored alpha.
    /// </summary>
    Mip,

    /// <summary>
    /// Alpha-blended direct volume rendering with opacity taken from the RGB luminance, i.e. the
    /// stored alpha is ignored. This is the "opaque" preview and the default, because volumes whose
    /// alpha is uniform would otherwise render as nothing.
    /// </summary>
    Rgb,

    /// <summary>Alpha-blended direct volume rendering using the stored alpha as density.</summary>
    Alpha,
}

/// <summary>
/// OpenGL viewport: a rotatable inside-out cubemap skybox and, for 3D textures, the whole volume
/// assembled in one ray-marched object that the camera orbits. Cross-section planes can be overlaid.
/// Degrades gracefully (leaves <see cref="Available"/> false) when the GL context or shaders are
/// unavailable.
/// </summary>
public sealed class GlPreviewControl : GLControl, IThemedControl
{
    private PreviewMode _mode = PreviewMode.Cube;

    private int _skyProgram = -1;
    private int _volumeProgram = -1;
    private int _sliceProgram = -1;
    private int _boundsProgram = -1;
    private int _vao;
    private int _cubeTexture = -1;
    private int _volumeTexture = -1;

    private IReadOnlyList<RgbaImage>? _pendingCubeFaces;
    private IReadOnlyList<FloatImage>? _pendingCubeFacesHdr;
    private RgbaImage? _pendingVolume;
    private bool _cubeDirty;
    private bool _cubeHdr;
    private bool _volumeDirty;
    private float _autoExposureScale = 1f;

    private readonly OrbitCamera _camera = new();
    private bool _dragging;
    private Point _lastMouse;
    private bool _lookInitialized;
    private Point _lookAnchor;

    public GlPreviewControl()
        : base(new GLControlSettings
        {
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            IsEventDriven = false,
        })
    {
        BackColor = ThemeManager.Palette.Viewer;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>The GL canvas keeps its dark backdrop in both themes.</summary>
    public void ApplyTheme(ThemePalette palette)
    {
        BackColor = palette.Viewer;
        Invalidate();
    }

    /// <summary>Arrow keys drive the look control, so they must reach the control instead of navigating.</summary>
    protected override bool IsInputKey(Keys keyData)
    {
        if (_mode == PreviewMode.Cube)
        {
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.Add:
                case Keys.Subtract:
                case Keys.Oemplus:
                case Keys.OemMinus:
                    return true;
            }
        }
        return base.IsInputKey(keyData);
    }

    public bool Available { get; private set; }

    public string? LastError { get; private set; }

    public PreviewMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Invalidate();
        }
    }

    public VolumeRenderMode RenderMode { get; set; } = VolumeRenderMode.Rgb;

    public float Density { get; set; } = 0.6f;

    /// <summary>
    /// Overlay the three cross-section planes. Off by default: the assembled volume is what the 3D
    /// view is for, and the planes are an inspection aid on top of it.
    /// </summary>
    public bool ShowSlices { get; set; }

    /// <summary>Draw the volume's bounding box, so an object is readable even where it is empty.</summary>
    public bool ShowBounds { get; set; } = true;

    /// <summary>Channel isolation, shared with the 2D viewer.</summary>
    public ImageChannelMode Channel { get; set; } = ImageChannelMode.Rgba;

    /// <summary>Ignore the stored alpha (the cubemap's RGBA view stays fully opaque).</summary>
    public bool OpaqueAlpha { get; set; }

    /// <summary>Display gamma applied after exposure; 1.0 shows the stored values unchanged.</summary>
    public float Gamma { get; set; } = 1f;

    /// <summary>Linear exposure multiplier applied before gamma; 1.0 leaves the values unchanged.</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    /// Normalise the sky from the cubemap's own values before the user's exposure and gamma are
    /// applied. On by default; the factor is derived from the uploaded faces, so it never adapts to
    /// (or sees) the display controls.
    /// </summary>
    public bool AutoExposure { get; set; } = true;

    /// <summary>The scene factor last derived from an uploaded cubemap; read-only diagnostics.</summary>
    public float AutoExposureScale => _autoExposureScale;

    public float SliceX { get; set; } = 0.5f;
    public float SliceY { get; set; } = 0.5f;
    public float SliceZ { get; set; } = 0.5f;

    /// <summary>Preview-only volume resolution cap; larger volumes are downscaled for the GPU.</summary>
    public int VolumeResolutionCap { get; set; } = 512;

    /// <summary>Orbit camera shared by the volume and its slice planes.</summary>
    public OrbitCamera Camera => _camera;

    public void SetCubeFaces(IReadOnlyList<RgbaImage> faces)
    {
        _pendingCubeFaces = faces;
        _pendingCubeFacesHdr = null;
        _cubeHdr = false;
        _cubeDirty = true;
        _mode = PreviewMode.Cube;
        if (!_lookInitialized)
        {
            _camera.ResetLook();
            _lookInitialized = true;
        }
        Invalidate();
    }

    /// <summary>
    /// Upload an HDR cubemap as float faces. Exposure is applied to the unclamped values and a
    /// tone map then brings them into display range, so the exposure control acts on the real HDR
    /// data instead of on values that were already clamped at decode time.
    /// </summary>
    public void SetCubeFacesHdr(IReadOnlyList<FloatImage> faces)
    {
        _pendingCubeFacesHdr = faces;
        _pendingCubeFaces = null;
        _cubeHdr = true;
        _cubeDirty = true;
        _mode = PreviewMode.Cube;
        if (!_lookInitialized)
        {
            _camera.ResetLook();
            _lookInitialized = true;
        }
        Invalidate();
    }

    public void SetVolume(RgbaImage volume)
    {
        _pendingVolume = volume;
        _volumeDirty = true;
        _mode = PreviewMode.Volume;
        // The volume has its own orbit framing; the next cubemap starts level again.
        _lookInitialized = false;
        Invalidate();
    }

    public void ResetCamera()
    {
        if (_mode == PreviewMode.Cube) _camera.ResetLook();
        else _camera.Reset();
        Invalidate();
    }

    protected override void OnLoad(EventArgs e)
    {
        string? skyError = null;
        string? volumeError = null;
        _uniformLocations.Clear();
        try
        {
            base.OnLoad(e);
            MakeCurrent();
            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);

            _skyProgram = BuildProgram(FullscreenVertexShader, SkyboxFragmentShader, out skyError);
            _volumeProgram = BuildProgram(FullscreenVertexShader, VolumeFragmentShader, out volumeError);
            _sliceProgram = BuildProgram(FullscreenVertexShader, SliceFragmentShader, out _);
            _boundsProgram = BuildProgram(BoundsVertexShader, BoundsFragmentShader, out _);

            if (_skyProgram < 0 || _volumeProgram < 0 || _sliceProgram < 0 || _boundsProgram < 0)
            {
                LastError = skyError ?? volumeError ?? "shader compilation failed";
                Available = false;
            }
            else
            {
                Available = true;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Available = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!IsHandleCreated)
        {
            base.OnPaint(e);
            return;
        }

        // A paint can be dispatched after the hosted window's GL context has gone away (for example
        // while the preview tabs swap the WindowsFormsHost, or during shutdown). Losing the context
        // here must not throw out of the paint handler and take the window down, so the frame is
        // skipped and the placeholder is painted instead; a later paint retries the context.
        if (!TryMakeCurrent())
        {
            DrawFallbackMessage("GPU preview unavailable:\ncontext is not current");
            return;
        }

        try
        {
            if (Available)
            {
                UploadPendingTextures();
                RenderScene(Math.Max(1, Width), Math.Max(1, Height));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Available = false;
        }

        try
        {
            SwapBuffers();
        }
        catch
        {
            // The context went away mid-frame; the next paint will fall back above.
        }

        if (!Available)
            DrawFallbackMessage();
    }

    /// <summary>
    /// Make the GL context current without letting a dead context throw. The control can outlive its
    /// context (a queued paint after the window is destroyed), so callers must tolerate a false result.
    /// </summary>
    private bool TryMakeCurrent()
    {
        try
        {
            MakeCurrent();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Render one frame into an offscreen buffer and read it back. Used by <c>--gltest</c> to verify
    /// the assembled view actually draws something without a human looking at the window.
    /// </summary>
    public Bitmap? RenderToBitmap(int width, int height)
    {
        if (!IsHandleCreated || !Available || width <= 0 || height <= 0) return null;
        if (!TryMakeCurrent()) return null;

        int framebuffer = GL.GenFramebuffer();
        int color = GL.GenTexture();
        int depth = GL.GenRenderbuffer();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

            GL.BindTexture(TextureTarget.Texture2D, color);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, color, 0);

            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depth);

            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            {
                LastError = "offscreen framebuffer incomplete";
                return null;
            }

            UploadPendingTextures();
            RenderScene(width, height);

            var pixels = new byte[width * height * 4];
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            return ToBitmap(pixels, width, height);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            GL.DeleteTexture(color);
            GL.DeleteRenderbuffer(depth);
        }
    }

    /// <summary>GL reads bottom-up; flip into a top-down 32bpp bitmap.</summary>
    private static Bitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                int source = (height - 1 - y) * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = source + x * 4;
                    int d = x * 4;
                    row[d + 0] = pixels[s + 2];
                    row[d + 1] = pixels[s + 1];
                    row[d + 2] = pixels[s + 0];
                    row[d + 3] = pixels[s + 3];
                }
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private void DrawFallbackMessage(string? message = null)
    {
        try
        {
            using var graphics = CreateGraphics();
            graphics.Clear(BackColor);
            using var font = new Font(FontFamily.GenericSansSerif, 9.5f);
            using var brush = new SolidBrush(Color.Gainsboro);
            graphics.DrawString(message ?? "GPU preview unavailable:\n" + (LastError ?? "unknown error"),
                font, brush, new PointF(10, 10));
        }
        catch
        {
            // Painting the placeholder is best-effort: a teardown race must not throw from a paint.
        }
    }

    private void UploadPendingTextures()
    {
        if (_cubeDirty && _pendingCubeFacesHdr is { Count: 6 })
        {
            _autoExposureScale = ComputeAutoExposure(_pendingCubeFacesHdr);
            _cubeTexture = UploadCubeMapHdr(_pendingCubeFacesHdr, _cubeTexture);
            _cubeDirty = false;
        }
        else if (_cubeDirty && _pendingCubeFaces is { Count: 6 })
        {
            _autoExposureScale = ComputeAutoExposure(_pendingCubeFaces);
            _cubeTexture = UploadCubeMap(_pendingCubeFaces, _cubeTexture);
            _cubeDirty = false;
        }

        if (_volumeDirty && _pendingVolume is not null)
        {
            var volume = _pendingVolume;
            if (Math.Max(volume.Width, Math.Max(volume.Height, volume.Depth)) > VolumeResolutionCap)
                volume = DownscaleVolume(volume, VolumeResolutionCap);
            _volumeTexture = UploadVolume(volume, _volumeTexture);
            _volumeDirty = false;
        }
    }

    private static RgbaImage DownscaleVolume(RgbaImage source, int cap)
    {
        int max = Math.Max(source.Width, Math.Max(source.Height, source.Depth));
        double scale = (double)cap / max;
        int w = Math.Max(1, (int)(source.Width * scale));
        int h = Math.Max(1, (int)(source.Height * scale));
        int d = Math.Max(1, (int)(source.Depth * scale));
        var result = new RgbaImage(w, h, d) { IsSrgb = source.IsSrgb };
        for (int z = 0; z < d; z++)
        {
            int sz = (int)((long)z * source.Depth / d);
            for (int y = 0; y < h; y++)
            {
                int sy = (int)((long)y * source.Height / h);
                for (int x = 0; x < w; x++)
                {
                    int sx = (int)((long)x * source.Width / w);
                    var (r, g, b, a) = source.GetPixel(sx, sy, sz);
                    result.SetPixel(x, y, r, g, b, a, z);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Scene factor for an 8-bit cubemap: the geometric mean of the stored luminance, mapped to middle
    /// grey. Sampled with a stride so a large face costs a bounded number of reads.
    /// </summary>
    private static float ComputeAutoExposure(IReadOnlyList<RgbaImage> faces) =>
        ComputeAutoExposure(faces, static face => face.Width, static face => face.Height,
            static (face, x, y) =>
            {
                var (r, g, b, _) = face.GetPixel(x, y);
                return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
            });

    /// <summary>Scene factor for a linear HDR cubemap; same statistic, on the unclamped float values.</summary>
    private static float ComputeAutoExposure(IReadOnlyList<FloatImage> faces) =>
        ComputeAutoExposure(faces, static face => face.Width, static face => face.Height,
            static (face, x, y) =>
            {
                var (r, g, b, _) = face.GetPixel(x, y);
                return Math.Max(0.0, 0.2126 * r + 0.7152 * g + 0.0722 * b);
            });

    /// <summary>
    /// Shared accumulator for both cube representations. Only the per-texel luminance differs between
    /// the stored-8-bit and linear-float paths, and both feed the one <c>uAutoExposureScale</c> uniform,
    /// so the statistic itself lives here once to keep the two paths from drifting apart.
    /// </summary>
    private static float ComputeAutoExposure<T>(
        IReadOnlyList<T> faces, Func<T, int> widthOf, Func<T, int> heightOf,
        Func<T, int, int, double> luminanceAt)
    {
        double logSum = 0;
        long count = 0;
        foreach (var face in faces)
        {
            int width = widthOf(face);
            int height = heightOf(face);
            int step = AutoExposureStep(width, height);
            for (int y = 0; y < height; y += step)
            {
                for (int x = 0; x < width; x += step)
                {
                    logSum += Math.Log(luminanceAt(face, x, y) + Xt1.Core.Imaging.AutoExposure.MinLuminance);
                    count++;
                }
            }
        }
        return Xt1.Core.Imaging.AutoExposure.Scale(logSum, count);
    }

    /// <summary>Sample enough texels to be stable without reading every pixel of a large face.</summary>
    private static int AutoExposureStep(int width, int height) =>
        Math.Max(1, (int)Math.Sqrt(width * (double)height / 4096.0));

    private static int UploadCubeMap(IReadOnlyList<RgbaImage> faces, int existing)
    {
        int texture = existing >= 0 ? existing : GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);
        for (int i = 0; i < 6; i++)
        {
            var face = faces[i];
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + i, 0, PixelInternalFormat.Rgba8,
                face.Width, face.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, face.Pixels);
        }
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private static int UploadCubeMapHdr(IReadOnlyList<FloatImage> faces, int existing)
    {
        int texture = existing >= 0 ? existing : GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);
        for (int i = 0; i < 6; i++)
        {
            var face = faces[i];
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + i, 0, PixelInternalFormat.Rgba16f,
                face.Width, face.Height, 0, PixelFormat.Rgba, PixelType.Float, face.Pixels);
        }
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private static int UploadVolume(RgbaImage volume, int existing)
    {
        int texture = existing >= 0 ? existing : GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, texture);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba8,
            volume.Width, volume.Height, volume.Depth, 0, PixelFormat.Rgba, PixelType.UnsignedByte, volume.Pixels);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private readonly Dictionary<(int Program, string Name), int> _uniformLocations = new();

    /// <summary>Cached uniform lookup; locations are resolved once per program instead of per frame.</summary>
    private int Loc(int program, string name)
    {
        if (_uniformLocations.TryGetValue((program, name), out int location)) return location;
        location = GL.GetUniformLocation(program, name);
        _uniformLocations[(program, name)] = location;
        return location;
    }

    /// <summary>Channel isolation is the same enum the 2D viewer uses; GLSL wants it as an index.</summary>
    private static int ChannelIndex(ImageChannelMode channel) => channel switch
    {
        ImageChannelMode.Red => 1,
        ImageChannelMode.Green => 2,
        ImageChannelMode.Blue => 3,
        ImageChannelMode.Alpha => 4,
        _ => 0,
    };

    private void RenderScene(int width, int height)
    {
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0.09f, 0.09f, 0.11f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.BindVertexArray(_vao);

        var rotation = _camera.Rotation();
        float aspect = (float)width / height;

        if (_mode == PreviewMode.Cube && _cubeTexture >= 0 && _skyProgram >= 0)
        {
            GL.Disable(EnableCap.DepthTest);
            // The skybox is opaque by default; with "Opaque Preview" off the faces' stored alpha is
            // honoured instead, so the toggle (and an alpha cubemap) are visible.
            if (OpaqueAlpha)
            {
                GL.Disable(EnableCap.Blend);
            }
            else
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            GL.UseProgram(_skyProgram);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.TextureCubeMap, _cubeTexture);
            GL.Uniform1(Loc(_skyProgram, "uCube"), 0);
            // The look camera needs its own rotation: the orbit one tilts the horizon (camera-right
            // Y = sin(pitch)*sin(yaw)), which is the "wonky" roll.
            GL.UniformMatrix3(Loc(_skyProgram, "uRot"), 1, false, _camera.LookRotation());
            GL.Uniform1(Loc(_skyProgram, "uFov"), _camera.Fov);
            GL.Uniform1(Loc(_skyProgram, "uAspect"), aspect);
            GL.Uniform1(Loc(_skyProgram, "uChannel"), ChannelIndex(Channel));
            GL.Uniform1(Loc(_skyProgram, "uOpaque"), OpaqueAlpha ? 1 : 0);
            GL.Uniform1(Loc(_skyProgram, "uHdr"), _cubeHdr ? 1 : 0);
            GL.Uniform1(Loc(_skyProgram, "uGamma"), Gamma <= 0f ? 1f : Gamma);
            GL.Uniform1(Loc(_skyProgram, "uExposure"), Exposure);
            GL.Uniform1(Loc(_skyProgram, "uAutoExposure"), AutoExposure ? 1 : 0);
            GL.Uniform1(Loc(_skyProgram, "uAutoExposureScale"), _autoExposureScale);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            return;
        }

        if (_mode != PreviewMode.Volume || _volumeTexture < 0 || _volumeProgram < 0) return;

        var halfExtent = GetHalfExtent();
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Bounds first so the blended volume draws over them.
        if (ShowBounds && _boundsProgram >= 0)
        {
            GL.UseProgram(_boundsProgram);
            SetCameraUniforms(_boundsProgram, rotation, aspect);
            GL.Uniform1(Loc(_boundsProgram, "uDistance"), _camera.Distance);
            GL.Uniform3(Loc(_boundsProgram, "uHalfExtent"), halfExtent.X, halfExtent.Y, halfExtent.Z);
            GL.DrawArrays(PrimitiveType.Lines, 0, 24);
        }

        GL.UseProgram(_volumeProgram);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _volumeTexture);
        GL.Uniform1(Loc(_volumeProgram, "uVolume"), 0);
        SetCameraUniforms(_volumeProgram, rotation, aspect);
        GL.Uniform1(Loc(_volumeProgram, "uDensity"), Density);
        GL.Uniform1(Loc(_volumeProgram, "uChannel"), ChannelIndex(Channel));
        GL.Uniform3(Loc(_volumeProgram, "uHalfExtent"), halfExtent.X, halfExtent.Y, halfExtent.Z);
        GL.Uniform1(Loc(_volumeProgram, "uMode"), RenderMode == VolumeRenderMode.Mip ? 0 : 1);
        GL.Uniform1(Loc(_volumeProgram, "uOpaque"), RenderMode == VolumeRenderMode.Rgb ? 1 : 0);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        if (ShowSlices && _sliceProgram >= 0)
        {
            GL.UseProgram(_sliceProgram);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture3D, _volumeTexture);
            GL.Uniform1(Loc(_sliceProgram, "uVolume"), 0);
            SetCameraUniforms(_sliceProgram, rotation, aspect);
            GL.Uniform1(Loc(_sliceProgram, "uChannel"), ChannelIndex(Channel));
            GL.Uniform1(Loc(_sliceProgram, "uOpaque"), RenderMode == VolumeRenderMode.Rgb ? 1 : 0);
            GL.Uniform3(Loc(_sliceProgram, "uHalfExtent"), halfExtent.X, halfExtent.Y, halfExtent.Z);

            float[] positions = { SliceX, SliceY, SliceZ };
            for (int axis = 0; axis < 3; axis++)
            {
                GL.Uniform1(Loc(_sliceProgram, "uAxis"), axis);
                GL.Uniform1(Loc(_sliceProgram, "uPos"), positions[axis]);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
        }
    }

    private void SetCameraUniforms(int program, float[] rotation, float aspect)
    {
        var eye = _camera.Eye();
        GL.UniformMatrix3(Loc(program, "uRot"), 1, false, rotation);
        GL.Uniform3(Loc(program, "uEye"), eye.X, eye.Y, eye.Z);
        GL.Uniform1(Loc(program, "uScale"), _camera.Scale);
        GL.Uniform1(Loc(program, "uAspect"), aspect);
    }

    private OpenTK.Mathematics.Vector3 GetHalfExtent()
    {
        if (_mode == PreviewMode.Volume && _pendingVolume is not null)
        {
            var v = _pendingVolume;
            float max = Math.Max(1, Math.Max(v.Width, Math.Max(v.Height, v.Depth)));
            return new OpenTK.Mathematics.Vector3(v.Width / max, v.Height / max, v.Depth / max);
        }
        return new OpenTK.Mathematics.Vector3(1f, 1f, 1f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        _lastMouse = e.Location;
        if (_mode == PreviewMode.Cube) _lookAnchor = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) _dragging = false;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        float dx = e.X - _lastMouse.X;
        float dy = e.Y - _lastMouse.Y;
        // Cube: look around from inside the skybox (pitch clamped, never rolls). Volume: orbit the
        // assembled object, where dragging should swing the model rather than turn a head.
        if (_mode == PreviewMode.Cube)
        {
            _camera.Look(dx, dy);
            // The look control is unbounded, so keep the drag alive at the viewport edge: when the
            // cursor reaches one, jump it back to the middle and continue from there, discarding the
            // warp's own delta. Without this a long turn just stops at the edge.
            var position = PointToClient(Cursor.Position);
            const int margin = 3;
            if (position.X <= margin || position.Y <= margin ||
                position.X >= ClientSize.Width - 1 - margin || position.Y >= ClientSize.Height - 1 - margin)
            {
                Cursor.Position = PointToScreen(_lookAnchor);
                e = new MouseEventArgs(e.Button, e.Clicks, _lookAnchor.X, _lookAnchor.Y, e.Delta);
            }
        }
        else
        {
            _camera.Orbit(dx, dy);
        }
        _lastMouse = e.Location;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_mode != PreviewMode.Cube) return;

        const float step = 0.08f;
        switch (e.KeyCode)
        {
            case Keys.Left:
                _camera.LookBy(step, 0f);       // turn left
                break;
            case Keys.Right:
                _camera.LookBy(-step, 0f);      // turn right
                break;
            case Keys.Up:
                _camera.LookBy(0f, step);       // look up
                break;
            case Keys.Down:
                _camera.LookBy(0f, -step);      // look down
                break;
            case Keys.Add:
            case Keys.Oemplus:
                _camera.ZoomFov(1f);
                break;
            case Keys.Subtract:
            case Keys.OemMinus:
                _camera.ZoomFov(-1f);
                break;
            case Keys.Home:
                ResetCamera();
                break;
            default:
                return;
        }
        e.Handled = true;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float notches = e.Delta > 0 ? 1f : -1f;
        if (_mode == PreviewMode.Cube) _camera.ZoomFov(notches);
        else _camera.Zoom(notches);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        ResetCamera();
    }

    private static int BuildProgram(string vertexSource, string fragmentSource, out string? error)
    {
        error = null;
        int vertex = Compile(ShaderType.VertexShader, vertexSource, out error);
        if (vertex < 0) return -1;
        int fragment = Compile(ShaderType.FragmentShader, fragmentSource, out error);
        if (fragment < 0) return -1;

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
        if (status == 0)
        {
            error = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            return -1;
        }
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    private static int Compile(ShaderType type, string source, out string? error)
    {
        error = null;
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            error = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            return -1;
        }
        return shader;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && IsHandleCreated)
        {
            try
            {
                MakeCurrent();
                foreach (var program in new[] { _skyProgram, _volumeProgram, _sliceProgram, _boundsProgram })
                    if (program >= 0) GL.DeleteProgram(program);
                if (_cubeTexture >= 0) GL.DeleteTexture(_cubeTexture);
                if (_volumeTexture >= 0) GL.DeleteTexture(_volumeTexture);
                if (_vao >= 0) GL.DeleteVertexArray(_vao);
            }
            catch
            {
                // ignore GL teardown failures
            }
        }
        base.Dispose(disposing);
    }

    private const string FullscreenVertexShader = @"#version 330 core
out vec2 vUv;
void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = p * 2.0 - 1.0;
    gl_Position = vec4(vUv, 0.0, 1.0);
}";

    private const string SkyboxFragmentShader = @"#version 330 core
in vec2 vUv;
out vec4 FragColor;
uniform samplerCube uCube;
uniform mat3 uRot;
uniform float uFov;
uniform float uAspect;
uniform int uChannel;  // 0 = RGBA, 1 = R, 2 = G, 3 = B, 4 = A
uniform int uOpaque;   // 1 = ignore the stored alpha
uniform int uHdr;      // 1 = values are linear HDR and need a tone map
uniform float uGamma;  // display gamma (1.0 = unchanged)
uniform float uExposure;
uniform int uAutoExposure;          // 1 = normalise the sky from the cubemap first
uniform float uAutoExposureScale;   // scene factor derived from the cubemap's own values
void main()
{
    vec3 dir = normalize(uRot * vec3(vUv.x * uAspect * uFov, vUv.y * uFov, -1.0));
    vec4 s = texture(uCube, dir);
    // Auto exposure normalises the scene and is derived from the cubemap alone, so it scales RGB
    // before channel isolation: it runs before the user's exposure/gamma and never adapts to them,
    // and it must not scale an isolated alpha value with an RGB-luminance factor.
    vec3 scene = s.rgb;
    if (uAutoExposure == 1) scene = max(scene * uAutoExposureScale, vec3(0.0));
    vec3 col = scene;
    if (uChannel == 1) col = vec3(scene.r);
    else if (uChannel == 2) col = vec3(scene.g);
    else if (uChannel == 3) col = vec3(scene.b);
    else if (uChannel == 4) col = vec3(s.a);
    // Exposure acts on the unclamped values; the HDR path then tone maps so lowering exposure
    // reveals highlights instead of just darkening a clipped image.
    col = max(col * uExposure, vec3(0.0));
    if (uHdr == 1) col = col / (1.0 + col);
    if (abs(uGamma - 1.0) > 0.001) col = pow(col, vec3(1.0 / uGamma));
    // Channel isolation is always opaque; the stored alpha is honoured only for the RGBA view
    // when the opaque-alpha toggle is off.
    float outAlpha = (uOpaque == 1 || uChannel != 0) ? 1.0 : s.a;
    FragColor = vec4(col, outAlpha);
}";

    /// <summary>
    /// Shared ray setup for the volume and slice shaders. The camera orbits the origin at
    /// uDistance, so rays start at uEye rather than inside the box.
    /// </summary>
    private const string CameraRayGlsl = @"
    vec3 rd = normalize(uRot * vec3(vUv.x * uAspect * uScale, vUv.y * uScale, -1.0));
    vec3 ro = uEye;";

    private const string VolumeFragmentShader = @"#version 330 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler3D uVolume;
uniform mat3 uRot;
uniform vec3 uEye;
uniform float uScale;
uniform float uAspect;
uniform float uDensity;
uniform vec3 uHalfExtent;
uniform int uMode;     // 0 = MIP, 1 = alpha blend
uniform int uOpaque;   // 1 = ignore stored alpha, use RGB luminance
uniform int uChannel;  // 0 = RGBA, 1 = R, 2 = G, 3 = B, 4 = A
void main()
{
    " + CameraRayGlsl + @"
    vec3 inv = 1.0 / rd;
    vec3 a = (-uHalfExtent - ro) * inv;
    vec3 b = ( uHalfExtent - ro) * inv;
    vec3 tmin = min(a, b);
    vec3 tmax = max(a, b);
    float tn = max(max(tmin.x, tmin.y), tmin.z);
    float tf = min(min(tmax.x, tmax.y), tmax.z);
    tn = max(tn, 0.0);
    if (tf <= tn) { FragColor = vec4(0.0); return; }

    const int STEPS = 192;
    float dt = (tf - tn) / float(STEPS);
    // Opacity correction. A volume that is flat (e.g. 256x256x16, half-extent 0.06 in Z) would
    // otherwise be all but invisible, because a ray crosses only a sliver of it; a cube of the same
    // data would look solid. Normalizing by the ray's path through the box keeps the total opacity of
    // a fully opaque volume the same for every shape, while the per-sample variation along the ray
    // still shows the internal structure.
    float correction = clamp(2.0 / max(tf - tn, 0.0001), 0.5, 8.0);
    vec3 p = ro + rd * (tn + dt * 0.5);
    vec4 acc = vec4(0.0);
    float maxv = 0.0;
    for (int i = 0; i < STEPS; i++)
    {
        vec3 uvw = p / uHalfExtent * 0.5 + 0.5;
        if (all(greaterThanEqual(uvw, vec3(0.0))) && all(lessThanEqual(uvw, vec3(1.0))))
        {
            vec4 s = texture(uVolume, uvw);
            float v = max(max(s.r, s.g), s.b);
            vec3 col = s.rgb;
            float alpha = s.a;
            if (uChannel == 1) { v = s.r; col = vec3(s.r); alpha = s.r; }
            else if (uChannel == 2) { v = s.g; col = vec3(s.g); alpha = s.g; }
            else if (uChannel == 3) { v = s.b; col = vec3(s.b); alpha = s.b; }
            else if (uChannel == 4) { v = s.a; col = vec3(s.a); alpha = s.a; }
            else if (uOpaque == 1) { alpha = v; }

            if (uMode == 0)
            {
                maxv = max(maxv, v);
            }
            else
            {
                float d = alpha * uDensity * dt * correction;
                acc.rgb += (1.0 - acc.a) * d * col;
                acc.a += (1.0 - acc.a) * d;
                if (acc.a > 0.98) break;
            }
        }
        p += rd * dt;
    }

    if (uMode == 0)
    {
        if (maxv <= 0.002) discard;
        FragColor = vec4(vec3(maxv), 1.0);
    }
    else
    {
        FragColor = vec4(acc.rgb, clamp(acc.a, 0.0, 1.0));
    }
}";

    private const string SliceFragmentShader = @"#version 330 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler3D uVolume;
uniform mat3 uRot;
uniform vec3 uEye;
uniform float uScale;
uniform float uAspect;
uniform vec3 uHalfExtent;
uniform int uAxis;
uniform float uPos;
uniform int uOpaque;
uniform int uChannel;
void main()
{
    " + CameraRayGlsl + @"
    float dc = (uAxis == 0) ? rd.x : ((uAxis == 1) ? rd.y : rd.z);
    float rc = (uAxis == 0) ? ro.x : ((uAxis == 1) ? ro.y : ro.z);
    float ec = (uAxis == 0) ? uHalfExtent.x : ((uAxis == 1) ? uHalfExtent.y : uHalfExtent.z);
    if (abs(dc) < 1e-6) { FragColor = vec4(0.0); return; }
    float t = (uPos * 2.0 * ec - ec - rc) / dc;
    if (t <= 0.0) { FragColor = vec4(0.0); return; }
    vec3 p = ro + rd * t;
    vec3 uvw = p / uHalfExtent * 0.5 + 0.5;
    if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
    {
        FragColor = vec4(0.0);
        return;
    }
    vec4 s = texture(uVolume, uvw);
    vec3 col = s.rgb;
    if (uChannel == 1) col = vec3(s.r);
    else if (uChannel == 2) col = vec3(s.g);
    else if (uChannel == 3) col = vec3(s.b);
    else if (uChannel == 4) col = vec3(s.a);
    FragColor = vec4(col, (uOpaque == 1 || uChannel != 0) ? 1.0 : 0.5);
}";

    private const string BoundsVertexShader = @"#version 330 core
uniform mat3 uRot;
uniform float uScale;
uniform float uAspect;
uniform vec3 uHalfExtent;
uniform float uDistance;
void main()
{
    int edge = gl_VertexID / 2;
    int k = gl_VertexID % 2;
    int axis = edge / 4;
    int idx = edge % 4;
    vec3 c;
    if (axis == 0)      c = vec3(float(k), float(idx & 1), float((idx >> 1) & 1));
    else if (axis == 1) c = vec3(float(idx & 1), float(k), float((idx >> 1) & 1));
    else                c = vec3(float(idx & 1), float((idx >> 1) & 1), float(k));

    vec3 rel = transpose(uRot) * ((c * 2.0 - 1.0) * uHalfExtent) - vec3(0.0, 0.0, uDistance);
    float w = -rel.z;
    if (w <= 0.0001)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }
    gl_Position = vec4(rel.x / (uAspect * uScale), rel.y / uScale, 0.0, w);
}";

    private const string BoundsFragmentShader = @"#version 330 core
out vec4 FragColor;
void main()
{
    FragColor = vec4(0.62, 0.62, 0.70, 0.45);
}";
}

