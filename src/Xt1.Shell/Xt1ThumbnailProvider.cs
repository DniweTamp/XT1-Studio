using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Xt1.Core.Containers;
using Xt1.Core.Imaging;

namespace Xt1.Shell;

/// <summary>
/// The shell thumbnail handler for loose <c>.xt1</c> files: Explorer asks it for a bitmap of a given
/// width and it decodes the cheapest mip that covers that width (see <see cref="Thumbnailer"/>).
/// <para>
/// Registered as an in-process COM server (<c>Xt1.Shell.comhost.dll</c>), created by Explorer and by
/// whatever else asks the shell for a thumbnail. Nothing here may throw across the COM boundary - an
/// escaped exception would take the host process down - so every entry point returns an HRESULT.
/// </para>
/// </summary>
[ComVisible(true)]
[Guid(ShellIds.HandlerClsid)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IThumbnailProvider))]
public sealed class Xt1ThumbnailProvider : IThumbnailProvider, IInitializeWithStream, IInitializeWithFile
{
    private const int MinSize = 16;

    private readonly object _gate = new();
    private EditableTexture? _texture;

    /// <summary>Explorer's normal path: it hands the handler a stream over the file.</summary>
    [PreserveSig]
    public int Initialize(IStream pstream, uint grfMode)
    {
        try
        {
            var texture = Parse(Streams.ReadAll(pstream));
            lock (_gate) _texture = texture;
            return HResult.Ok;
        }
        catch
        {
            return HResult.Fail;
        }
    }

    /// <summary>Path-based initialization, for callers that have a file rather than a stream.</summary>
    [PreserveSig]
    public int Initialize(string pszFilePath, uint grfMode)
    {
        try
        {
            var texture = Parse(Streams.ReadFile(pszFilePath));
            lock (_gate) _texture = texture;
            return HResult.Ok;
        }
        catch
        {
            return HResult.Fail;
        }
    }

    [PreserveSig]
    public int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = WtsAlphaType.Unknown;

        try
        {
            EditableTexture? texture;
            lock (_gate) texture = _texture;
            if (texture is null) return HResult.Fail;

            var size = (int)Math.Clamp(cx < MinSize ? Thumbnailer.DefaultSize : cx,
                (uint)MinSize, (uint)Thumbnailer.MaxSize);

            var image = Thumbnailer.Render(texture, new ThumbnailOptions(size), out _, out _);
            if (image is null) return HResult.Fail;

            phbmp = DibBitmap.Create(image, out bool hasAlpha);
            pdwAlpha = hasAlpha ? WtsAlphaType.Argb : WtsAlphaType.Rgb;
            return HResult.Ok;
        }
        catch
        {
            // Nothing gets past this line: a failure here must not escape into the host process.
            if (phbmp != IntPtr.Zero)
            {
                DibBitmap.Delete(phbmp);
                phbmp = IntPtr.Zero;
            }
            pdwAlpha = WtsAlphaType.Unknown;
            return HResult.Fail;
        }
    }

    private static EditableTexture Parse(byte[] bytes)
    {
        if (!RawXt1.HasMagic(bytes)) throw new InvalidDataException("not a loose XT1 texture");
        return EditableTexture.FromRawReadOnly(RawXt1.ParseReadOnly(bytes));
    }
}
