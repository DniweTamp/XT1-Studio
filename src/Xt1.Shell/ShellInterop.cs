using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Xt1.Shell;

/// <summary>Well-known shell identifiers this handler is built around.</summary>
public static class ShellIds
{
    /// <summary>The CLSID of <see cref="Xt1ThumbnailProvider"/>. Stable: registrations point at it.</summary>
    public const string HandlerClsid = "4F1C6E2D-8A73-4B90-B5C4-9E2A7D3F6C18";

    /// <summary>
    /// The thumbnail-handler shell extension interface <c>{e357fccd-a995-4576-b01f-234630154e96}</c>,
    /// used as the value name under <c>ShellEx</c> for a file type.
    /// </summary>
    public const string ThumbnailProviderInterface = "e357fccd-a995-4576-b01f-234630154e96";

    /// <summary>The extension this handler is registered for.</summary>
    public const string Extension = ".xt1";
}

/// <summary>
/// <c>IThumbnailProvider</c>. <paramref name="cx"/> is the requested square width; the returned
/// HBITMAP is 32bpp with premultiplied alpha and must not exceed that width.
/// </summary>
[ComImport]
[Guid("e357fccd-a995-4576-b01f-234630154e96")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    [PreserveSig]
    int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
}

/// <summary><c>IInitializeWithStream</c>: what Explorer normally hands a thumbnail handler.</summary>
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig]
    int Initialize(IStream pstream, uint grfMode);
}

/// <summary>
/// <c>IInitializeWithFile</c>. Some callers (and the in-process self-check) have a path rather than
/// a stream; implementing both means whichever the caller prefers works.
/// </summary>
[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    [PreserveSig]
    int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

/// <summary><c>WTS_ALPHATYPE</c>: what the returned bitmap's alpha channel means.</summary>
public static class WtsAlphaType
{
    public const uint Unknown = 0;
    public const uint Rgb = 1;
    public const uint Argb = 2;
}

internal static class HResult
{
    public const int Ok = 0;

    /// <summary>Reported when there is no thumbnail to give; Explorer falls back to the file icon.</summary>
    public const int Fail = unchecked((int)0x80004005);
}

internal static class Streams
{
    /// <summary>
    /// Loose textures in the game are far below this. Keep shell extraction bounded: this data is
    /// attacker-controlled and the handler runs inside a shared COM surrogate.
    /// </summary>
    public const int MaxBytes = 128 * 1024 * 1024;

    /// <summary>Read an <c>IStream</c> from the start into a byte array.</summary>
    public static byte[] ReadAll(IStream stream)
    {
        try
        {
            stream.Seek(0, 0, IntPtr.Zero); // STREAM_SEEK_SET; a caller may hand over a used stream
        }
        catch
        {
            // A non-seekable stream simply continues from wherever it is.
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        IntPtr read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                int remaining = MaxBytes - checked((int)buffer.Length);
                if (remaining == 0)
                {
                    stream.Read(chunk, 1, read);
                    if (Marshal.ReadInt32(read) > 0)
                        throw new InvalidDataException("stream is too large to thumbnail");
                    break;
                }

                stream.Read(chunk, Math.Min(chunk.Length, remaining), read);
                int count = Marshal.ReadInt32(read);
                if (count <= 0) break;
                buffer.Write(chunk, 0, count);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }

        return buffer.ToArray();
    }

    /// <summary>Read a file with the same hard bound as the stream initializer.</summary>
    public static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException("file is too large to thumbnail");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
