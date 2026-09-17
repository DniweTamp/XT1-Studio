using System.Runtime.InteropServices;
using Xt1.Core.Imaging;
using Xt1.Core.Interop;

namespace Xt1.Core.Nvtt;

/// <summary>
/// In-process NVIDIA Texture Tools 3.2.5 interop (<c>nvtt30205.dll</c> + nvtt_wrapper.h).
/// Encodes one surface (one mip, one cube face, one volume slice) at a time so the caller
/// controls swizzle/packing. Output bytes are captured in memory via an output handler, so no
/// temporary files are produced.
/// </summary>
public sealed class NvttNative : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BeginImageFn(int size, int width, int height, int depth, int face, int mipLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OutputFn(IntPtr data, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndImageFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateSurfaceFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroySurfaceFn(IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetImageDataFn(IntPtr surface, int format, int w, int h, int d,
        IntPtr data, int unsignedToSigned, IntPtr timingContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateContextFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyContextFn(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateCompressionOptionsFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyCompressionOptionsFn(IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCompressionFormatFn(IntPtr options, int format);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCompressionQualityFn(IntPtr options, int quality);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCompressionQuantizationFn(IntPtr options, int colorDithering, int alphaDithering,
        int binaryAlpha, int alphaThreshold);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateOutputOptionsFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyOutputOptionsFn(IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOutputHandlerFn(IntPtr options, BeginImageFn begin, OutputFn output, EndImageFn end);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOutputHeaderFn(IntPtr options, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOutputSrgbFn(IntPtr options, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOutputContainerFn(IntPtr options, int container);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSurfaceAlphaModeFn(IntPtr surface, int alphaMode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ContextOutputHeaderFn(IntPtr context, IntPtr surface, int mipCount,
        IntPtr compressionOptions, IntPtr outputOptions);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ContextCompressFn(IntPtr context, IntPtr surface, int face, int mip,
        IntPtr compressionOptions, IntPtr outputOptions);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ErrorStringFn(int error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionFn();

    private readonly IntPtr _handle;
    private readonly CreateSurfaceFn _createSurface;
    private readonly DestroySurfaceFn _destroySurface;
    private readonly SetImageDataFn _setImageData;
    private readonly CreateContextFn _createContext;
    private readonly DestroyContextFn _destroyContext;
    private readonly CreateCompressionOptionsFn _createCompressionOptions;
    private readonly DestroyCompressionOptionsFn _destroyCompressionOptions;
    private readonly SetCompressionFormatFn _setCompressionFormat;
    private readonly SetCompressionQualityFn _setCompressionQuality;
    private readonly SetCompressionQuantizationFn _setCompressionQuantization;
    private readonly CreateOutputOptionsFn _createOutputOptions;
    private readonly DestroyOutputOptionsFn _destroyOutputOptions;
    private readonly SetOutputHandlerFn _setOutputHandler;
    private readonly SetOutputHeaderFn _setOutputHeader;
    private readonly SetOutputSrgbFn _setOutputSrgb;
    private readonly SetOutputContainerFn _setOutputContainer;
    private readonly SetSurfaceAlphaModeFn _setSurfaceAlphaMode;
    private readonly ContextOutputHeaderFn _outputHeader;
    private readonly ContextCompressFn _compress;

    public string LibraryPath { get; }
    public uint Version { get; }

    private NvttNative(IntPtr handle, string path, uint version,
        CreateSurfaceFn createSurface, DestroySurfaceFn destroySurface, SetImageDataFn setImageData,
        CreateContextFn createContext, DestroyContextFn destroyContext,
        CreateCompressionOptionsFn createCompressionOptions, DestroyCompressionOptionsFn destroyCompressionOptions,
        SetCompressionFormatFn setCompressionFormat, SetCompressionQualityFn setCompressionQuality,
        SetCompressionQuantizationFn setCompressionQuantization,
        CreateOutputOptionsFn createOutputOptions, DestroyOutputOptionsFn destroyOutputOptions,
        SetOutputHandlerFn setOutputHandler, SetOutputHeaderFn setOutputHeader,
        SetOutputSrgbFn setOutputSrgb, SetOutputContainerFn setOutputContainer,
        SetSurfaceAlphaModeFn setSurfaceAlphaMode,
        ContextOutputHeaderFn outputHeader, ContextCompressFn compress)
    {
        _handle = handle;
        LibraryPath = path;
        Version = version;
        _createSurface = createSurface;
        _destroySurface = destroySurface;
        _setImageData = setImageData;
        _createContext = createContext;
        _destroyContext = destroyContext;
        _createCompressionOptions = createCompressionOptions;
        _destroyCompressionOptions = destroyCompressionOptions;
        _setCompressionFormat = setCompressionFormat;
        _setCompressionQuality = setCompressionQuality;
        _setCompressionQuantization = setCompressionQuantization;
        _createOutputOptions = createOutputOptions;
        _destroyOutputOptions = destroyOutputOptions;
        _setOutputHandler = setOutputHandler;
        _setOutputHeader = setOutputHeader;
        _setOutputSrgb = setOutputSrgb;
        _setOutputContainer = setOutputContainer;
        _setSurfaceAlphaMode = setSurfaceAlphaMode;
        _outputHeader = outputHeader;
        _compress = compress;
    }

    public static NvttNative? TryCreate(out string? error, string? explicitPath = null)
    {
        error = null;
        var path = ToolLocator.Find(ToolLocator.NvttDllFileName, explicitPath);
        if (path is null)
        {
            error = "nvtt30205.dll not found";
            return null;
        }

        if (!NativeLibrary.TryLoad(path, out var handle))
        {
            error = $"failed to load {path} (missing FreeImage.dll / vcomp140.dll?)";
            return null;
        }

        try
        {
            return new NvttNative(handle, path,
                Bind<VersionFn>(handle, "nvttVersion")(),
                Bind<CreateSurfaceFn>(handle, "nvttCreateSurface"),
                Bind<DestroySurfaceFn>(handle, "nvttDestroySurface"),
                Bind<SetImageDataFn>(handle, "nvttSurfaceSetImageData"),
                Bind<CreateContextFn>(handle, "nvttCreateContext"),
                Bind<DestroyContextFn>(handle, "nvttDestroyContext"),
                Bind<CreateCompressionOptionsFn>(handle, "nvttCreateCompressionOptions"),
                Bind<DestroyCompressionOptionsFn>(handle, "nvttDestroyCompressionOptions"),
                Bind<SetCompressionFormatFn>(handle, "nvttSetCompressionOptionsFormat"),
                Bind<SetCompressionQualityFn>(handle, "nvttSetCompressionOptionsQuality"),
                Bind<SetCompressionQuantizationFn>(handle, "nvttSetCompressionOptionsQuantization"),
                Bind<CreateOutputOptionsFn>(handle, "nvttCreateOutputOptions"),
                Bind<DestroyOutputOptionsFn>(handle, "nvttDestroyOutputOptions"),
                Bind<SetOutputHandlerFn>(handle, "nvttSetOutputOptionsOutputHandler"),
                Bind<SetOutputHeaderFn>(handle, "nvttSetOutputOptionsOutputHeader"),
                Bind<SetOutputSrgbFn>(handle, "nvttSetOutputOptionsSrgbFlag"),
                Bind<SetOutputContainerFn>(handle, "nvttSetOutputOptionsContainer"),
                Bind<SetSurfaceAlphaModeFn>(handle, "nvttSetSurfaceAlphaMode"),
                Bind<ContextOutputHeaderFn>(handle, "nvttContextOutputHeader"),
                Bind<ContextCompressFn>(handle, "nvttContextCompress"));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            NativeLibrary.Free(handle);
            return null;
        }
    }

    private static T Bind<T>(IntPtr handle, string export) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, export, out var address))
            throw new MissingMethodException($"nvtt export '{export}' not found");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// Encode a single RGBA8 surface to the requested NVTT format. Returns the raw compressed
    /// block bytes (no DDS header).
    /// </summary>
    public byte[] EncodeSurface(RgbaImage image, NvttFormat format, NvttQuality quality,
        bool srgb, bool binaryAlpha = false, int alphaThreshold = 127, bool hasAlpha = true)
    {
        // NVTT's 8-bit input expects BGRA channel order.
        var bgra = ToBgra(image.Pixels);

        IntPtr surface = IntPtr.Zero, context = IntPtr.Zero, compression = IntPtr.Zero, output = IntPtr.Zero;
        var sink = new CaptureSink();
        try
        {
            surface = _createSurface();
            context = _createContext();
            compression = _createCompressionOptions();
            output = _createOutputOptions();

            _setSurfaceAlphaMode(surface, hasAlpha ? 1 : 0); // 1 = Transparency

            var bgraHandle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                bool ok = _setImageData(surface, (int)NvttInputFormat.Bgra8Ub,
                    image.Width, image.Height, image.Depth,
                    bgraHandle.AddrOfPinnedObject(), 0, IntPtr.Zero) != 0;
                if (!ok)
                    throw new InvalidOperationException("nvttSurfaceSetImageData failed");
            }
            finally
            {
                bgraHandle.Free();
            }

            _setCompressionFormat(compression, (int)format);
            _setCompressionQuality(compression, (int)quality);
            _setCompressionQuantization(compression, 0, 0, binaryAlpha ? 1 : 0, alphaThreshold);

            _setOutputContainer(output, (int)NvttContainer.Dds10);
            _setOutputHeader(output, 0);
            _setOutputSrgb(output, srgb ? 1 : 0);
            _setOutputHandler(output, sink.Begin, sink.Write, sink.End);
            GC.KeepAlive(sink);

            if (_outputHeader(context, surface, 1, compression, output) == 0)
                throw new InvalidOperationException("nvttContextOutputHeader failed");
            if (_compress(context, surface, 0, 0, compression, output) == 0)
                throw new InvalidOperationException("nvttContextCompress failed");

            return sink.ToArray();
        }
        finally
        {
            if (output != IntPtr.Zero) _destroyOutputOptions(output);
            if (compression != IntPtr.Zero) _destroyCompressionOptions(compression);
            if (context != IntPtr.Zero) _destroyContext(context);
            if (surface != IntPtr.Zero) _destroySurface(surface);
        }
    }

    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            bgra[i + 0] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i + 0];
            bgra[i + 3] = rgba[i + 3];
        }
        return bgra;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            NativeLibrary.Free(_handle);
    }

    private sealed class CaptureSink
    {
        private readonly MemoryStream _stream = new();
        private byte[] _buffer = Array.Empty<byte>();

        public readonly BeginImageFn Begin;
        public readonly OutputFn Write;
        public readonly EndImageFn End;

        public CaptureSink()
        {
            Begin = (size, width, height, depth, face, mip) =>
            {
                _stream.SetLength(0);
                _stream.Position = 0;
            };
            Write = (data, size) =>
            {
                if (size <= 0) return 1;
                if (_buffer.Length < size) _buffer = new byte[size];
                Marshal.Copy(data, _buffer, 0, size);
                _stream.Write(_buffer, 0, size);
                return 1;
            };
            End = () => { };
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
