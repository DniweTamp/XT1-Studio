using System.Runtime.InteropServices;

namespace Xt1.Core.Interop;

/// <summary>
/// Oodle (Kraken) codec via <c>oo2core_7_win64.dll</c>. The DLL is optional: when absent the
/// caller falls back to raw payloads and warns. Decompression is the primary path (reading
/// `.dtt` / packed payloads); compression is best-effort.
/// </summary>
public sealed class OodleCodec : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleDecompressFn(
        IntPtr compBuf, long compSize, IntPtr rawBuf, long rawSize,
        int fuzzSafe, int checkCRC, int verbosity,
        IntPtr decBufBase, long decBufSize,
        IntPtr callback, IntPtr callbackUserData,
        IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleCompressFn(
        int compressor, IntPtr rawBuf, long rawSize, int level, IntPtr options,
        IntPtr matcher, IntPtr matcherMem, IntPtr floatMem, long stripSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CompressOptionsGetDefaultFn(int compressor, int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long CompressedBufferSizeNeededFn(int compressor, long rawSize);

    // OodleLZ_Compressor
    public const int CompressorKraken = 4;

    // OodleLZ_CompressionLevel
    public const int LevelNormal = 4;

    private readonly IntPtr _handle;
    private readonly OodleDecompressFn? _decompress;
    private readonly OodleCompressFn? _compress;
    private readonly CompressOptionsGetDefaultFn? _optionsGetDefault;
    private readonly CompressedBufferSizeNeededFn? _bufferSizeNeeded;

    public string LibraryPath { get; }

    private int _compressionProbe = -1;

    /// <summary>
    /// Whether this oo2core build actually compresses. Many game-shipped builds are
    /// decompress-only; <c>OodleLZ_Compress</c> exists but always returns 0. Probed once.
    /// </summary>
    public bool SupportsCompression => ProbeCompression();

    private OodleCodec(IntPtr handle, string path,
        OodleDecompressFn? decompress, OodleCompressFn? compress,
        CompressOptionsGetDefaultFn? optionsGetDefault, CompressedBufferSizeNeededFn? bufferSizeNeeded)
    {
        _handle = handle;
        LibraryPath = path;
        _decompress = decompress;
        _compress = compress;
        _optionsGetDefault = optionsGetDefault;
        _bufferSizeNeeded = bufferSizeNeeded;
    }

    /// <summary>Load the Oodle DLL, or return null when it cannot be found/loaded.</summary>
    public static OodleCodec? TryCreate(out string? error, string? explicitPath = null)
    {
        error = null;
        var path = ToolLocator.Find(ToolLocator.OodleFileName, explicitPath);
        if (path is null)
        {
            error = "oo2core DLL not found";
            return null;
        }

        if (!NativeLibrary.TryLoad(path, out var handle))
        {
            error = $"failed to load {path}";
            return null;
        }

        try
        {
            var decompress = Bind<OodleDecompressFn>(handle, "OodleLZ_Decompress");
            if (decompress is null)
            {
                error = "OodleLZ_Decompress export not found";
                NativeLibrary.Free(handle);
                return null;
            }

            var compress = Bind<OodleCompressFn>(handle, "OodleLZ_Compress");
            var defaults = Bind<CompressOptionsGetDefaultFn>(handle, "OodleLZ_CompressOptions_GetDefault");
            var sizeNeeded = Bind<CompressedBufferSizeNeededFn>(handle, "OodleLZ_GetCompressedBufferSizeNeeded");
            return new OodleCodec(handle, path, decompress, compress, defaults, sizeNeeded);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            NativeLibrary.Free(handle);
            return null;
        }
    }

    private static T? Bind<T>(IntPtr handle, string export) where T : Delegate
    {
        return NativeLibrary.TryGetExport(handle, export, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
    }

    /// <summary>Decompress an Oodle (Kraken) buffer to exactly <paramref name="rawSize"/> bytes.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> compressed, long rawSize)
    {
        if (_decompress is null) throw new InvalidOperationException("Oodle decompression unavailable");
        if (rawSize <= 0) throw new ArgumentOutOfRangeException(nameof(rawSize));

        var result = new byte[rawSize];
        var comp = Marshal.AllocHGlobal(compressed.Length);
        var raw = Marshal.AllocHGlobal((int)rawSize);
        try
        {
            Marshal.Copy(compressed.ToArray(), 0, comp, compressed.Length);
            long written = _decompress(comp, compressed.Length, raw, rawSize,
                fuzzSafe: 1, checkCRC: 0, verbosity: 0,
                IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0);
            if (written <= 0)
                throw new InvalidDataException($"Oodle decompression failed (returned {written})");
            Marshal.Copy(raw, result, 0, (int)Math.Min(written, rawSize));
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(comp);
            Marshal.FreeHGlobal(raw);
        }
    }

    /// <summary>Compress with Oodle Kraken. Throws when the loaded build is decompress-only.</summary>
    public byte[] Compress(ReadOnlySpan<byte> raw, int compressor = CompressorKraken, int level = LevelNormal)
    {
        if (_compress is null) throw new InvalidOperationException("Oodle compression unavailable");
        if (!ProbeCompression())
            throw new NotSupportedException(
                "this oo2core build does not support compression (decompress-only DLL)");
        return CompressImpl(raw, compressor, level);
    }

    private bool ProbeCompression()
    {
        if (_compressionProbe >= 0) return _compressionProbe == 1;
        if (_compress is null)
        {
            _compressionProbe = 0;
            return false;
        }

        try
        {
            var probe = new byte[256];
            for (int i = 0; i < probe.Length; i++) probe[i] = (byte)(i * 7);
            var compressed = CompressImpl(probe, CompressorKraken, LevelNormal);
            bool ok = compressed.Length > 0 && compressed.Length < probe.Length;
            _compressionProbe = ok ? 1 : 0;
        }
        catch
        {
            _compressionProbe = 0;
        }
        return _compressionProbe == 1;
    }

    private byte[] CompressImpl(ReadOnlySpan<byte> raw, int compressor, int level)
    {
        var compress = _compress ?? throw new InvalidOperationException("Oodle compression unavailable");
        long capacity = _bufferSizeNeeded?.Invoke(compressor, raw.Length) ?? (raw.Length + raw.Length / 2 + 65536);
        if (capacity <= 0) capacity = raw.Length + raw.Length / 2 + 65536;

        var rawPtr = Marshal.AllocHGlobal(Math.Max(1, raw.Length));
        var outPtr = Marshal.AllocHGlobal((int)capacity);
        try
        {
            if (raw.Length > 0) Marshal.Copy(raw.ToArray(), 0, rawPtr, raw.Length);
            IntPtr options = _optionsGetDefault?.Invoke(compressor, level) ?? IntPtr.Zero;
            long written = compress(compressor, rawPtr, raw.Length, level, options,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
            if (written <= 0)
                throw new InvalidDataException($"Oodle compression failed (returned {written})");
            var result = new byte[written];
            Marshal.Copy(outPtr, result, 0, (int)written);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(rawPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    /// <summary>Round-trip self test (compress then decompress).</summary>
    public bool SelfTest(out string message)
    {
        try
        {
            var data = new byte[4096];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + (i >> 3));
            var compressed = Compress(data);
            var back = Decompress(compressed, data.Length);
            if (back.AsSpan().SequenceEqual(data))
            {
                message = $"OK ({data.Length} -> {compressed.Length} bytes)";
                return true;
            }
            message = "round-trip mismatch";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            NativeLibrary.Free(_handle);
    }
}
