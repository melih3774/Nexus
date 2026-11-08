using System.Diagnostics;
using System.Runtime.Versioning;

using PcCore.Windows.Interop;

using static PcCore.Windows.Interop.D3D11VideoInterop;
using static PcCore.Windows.Interop.DxgiInterop;

namespace PcCore.Windows.Media;

/// <summary>
/// Scales a BGRA picture and converts it to NV12 on the GPU, with the Direct3D 11 video processor.
/// <para>
/// Used only to measure, for now: <c>--selftest-video --gpu-scale</c> compares its speed and its
/// handling of small text against <see cref="Nv12Converter"/>, because moving the stream off the CPU
/// is only worth doing if text survives the driver's scaler. Which filter that scaler uses is up to
/// the driver, so the answer can differ between GPU vendors.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class D3D11FrameScaler : IDisposable
{
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _videoDevice;
    private IntPtr _videoContext;
    private IntPtr _enumerator;
    private IntPtr _processor;
    private IntPtr _source;
    private IntPtr _sourceStaging;
    private IntPtr _target;
    private IntPtr _targetStaging;
    private IntPtr _inputView;
    private IntPtr _outputView;

    private D3D11FrameScaler(int sourceWidth, int sourceHeight, int width, int height)
    {
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _width = width;
        _height = height;
    }

    /// <summary>The adapter this is running on.</summary>
    public string Adapter { get; private set; } = "";

    /// <summary>Builds a scaler on the first adapter that drives a display.</summary>
    /// <exception cref="InvalidOperationException">When this GPU cannot do it, saying which step refused.</exception>
    public static D3D11FrameScaler Create(int sourceWidth, int sourceHeight, int width, int height, bool optimalQuality)
    {
        var scaler = new D3D11FrameScaler(sourceWidth, sourceHeight, width, height);

        try
        {
            scaler.Initialise(optimalQuality);
            return scaler;
        }
        catch
        {
            scaler.Dispose();
            throw;
        }
    }

    private void Initialise(bool optimalQuality)
    {
        var adapter = FirstAdapterWithAnOutput();

        try
        {
            Adapter = AdapterName(adapter);

            int[] featureLevels = [0xb000, 0xa100, 0xa000];

            Check(
                D3D11CreateDevice(
                    adapter,
                    D3DDriverTypeUnknown,
                    IntPtr.Zero,
                    DeviceCreateBgraSupport | DeviceCreateVideoSupport,
                    featureLevels,
                    (uint)featureLevels.Length,
                    D3D11SdkVersion,
                    out _device,
                    out _,
                    out _context),
                "D3D11CreateDevice");
        }
        finally
        {
            Release(ref adapter);
        }

        var deviceIid = IidVideoDevice;
        Check(QueryInterface(_device, ref deviceIid, out _videoDevice), "ID3D11VideoDevice");

        var contextIid = IidVideoContext;
        Check(QueryInterface(_context, ref contextIid, out _videoContext), "ID3D11VideoContext");

        var content = new VideoProcessorContentDesc
        {
            InputFrameFormat = FrameFormatProgressive,
            InputRateNumerator = 30,
            InputRateDenominator = 1,
            InputWidth = (uint)_sourceWidth,
            InputHeight = (uint)_sourceHeight,
            OutputRateNumerator = 30,
            OutputRateDenominator = 1,
            OutputWidth = (uint)_width,
            OutputHeight = (uint)_height,
            Usage = optimalQuality ? VideoUsageOptimalQuality : VideoUsageOptimalSpeed,
        };

        Check(CreateEnumerator(_videoDevice, ref content, out _enumerator), "CreateVideoProcessorEnumerator");
        Check(CreateVideoProcessor(_videoDevice, _enumerator, 0, out _processor), "CreateVideoProcessor");

        _source = Texture(_sourceWidth, _sourceHeight, DxgiFormatB8G8R8A8Unorm, UsageDefault, BindRenderTarget | BindShaderResource, 0);
        _sourceStaging = Texture(_sourceWidth, _sourceHeight, DxgiFormatB8G8R8A8Unorm, D3D11UsageStaging, 0, D3D11CpuAccessRead);
        _target = Texture(_width, _height, DxgiFormatNv12, UsageDefault, BindRenderTarget, 0);
        _targetStaging = Texture(_width, _height, DxgiFormatNv12, D3D11UsageStaging, 0, D3D11CpuAccessRead);

        Check(CreateInputView(_videoDevice, _source, _enumerator, out _inputView), "CreateVideoProcessorInputView");
        Check(CreateOutputView(_videoDevice, _target, _enumerator, out _outputView), "CreateVideoProcessorOutputView");

        // Full-range RGB in. The same limited-range matrix the CPU converter and the encoder's media
        // type use out, or the two paths would differ in colour and the comparison would mean nothing.
        const uint LimitedRange = 1 << 4;
        const uint Bt709 = 1 << 2;

        SetStreamColorSpace(_videoContext, _processor, 0, 0);
        SetOutputColorSpace(_videoContext, _processor, LimitedRange | (Nv12Converter.UsesBt709(_height) ? Bt709 : 0));
        SetStreamFrameFormat(_videoContext, _processor, 0, FrameFormatProgressive);

        // Automatic processing is the driver's licence to denoise and sharpen. A desktop has no noise,
        // and a ringing halo around text is not an improvement.
        SetStreamAutoProcessing(_videoContext, _processor, 0, false);
    }

    /// <summary>Copies a picture to the GPU, standing in for the texture a capture would already have.</summary>
    public void Upload(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        fixed (byte* source = bgra)
        {
            UpdateSubresource(_context, _source, source, (uint)(_sourceWidth * 4));
        }

    }

    /// <summary>
    /// Runs the scale and conversion and returns milliseconds, measured to the point the CPU can read
    /// the result. An event query on this context completed before the video engine had finished, so
    /// only a mapped copy of the output is proof that the work is done.
    /// </summary>
    public double Process()
    {
        var started = Stopwatch.GetTimestamp();

        Check(Blt(_videoContext, _processor, _outputView, _inputView), "VideoProcessorBlt");
        CopyResource(_context, _targetStaging, _target);
        Check(Map(_context, _targetStaging, out _), "Map");
        Unmap(_context, _targetStaging);

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// What today's capture path does before the CPU converter can start: copies the desktop to a
    /// staging texture, maps it and reads it out. Returns milliseconds.
    /// </summary>
    public double ReadBackSource(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        var started = Stopwatch.GetTimestamp();

        CopyResource(_context, _sourceStaging, _source);
        Check(Map(_context, _sourceStaging, out var mapped), "Map");

        try
        {
            for (var y = 0; y < _sourceHeight; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.Data + (y * mapped.RowPitch), _sourceWidth * 4)
                    .CopyTo(bgra.AsSpan(y * _sourceWidth * 4));
            }
        }
        finally
        {
            Unmap(_context, _sourceStaging);
        }

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Reads the result into tightly packed NV12, so that it can be compared.</summary>
    public void ReadBackResult(byte[] nv12)
    {
        ArgumentNullException.ThrowIfNull(nv12);

        CopyResource(_context, _targetStaging, _target);
        Check(Map(_context, _targetStaging, out var mapped), "Map");

        try
        {
            var data = (byte*)mapped.Data;

            for (var y = 0; y < _height; y++)
            {
                new ReadOnlySpan<byte>(data + (y * mapped.RowPitch), _width).CopyTo(nv12.AsSpan(y * _width));
            }

            // Both planes share one mapping: chroma starts one texture height below luma.
            var chroma = data + (_height * mapped.RowPitch);

            for (var y = 0; y < _height / 2; y++)
            {
                new ReadOnlySpan<byte>(chroma + (y * mapped.RowPitch), _width)
                    .CopyTo(nv12.AsSpan((_height * _width) + (y * _width)));
            }
        }
        finally
        {
            Unmap(_context, _targetStaging);
        }
    }

    private IntPtr Texture(int width, int height, uint format, uint usage, uint bind, uint cpu)
    {
        var description = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            Usage = usage,
            BindFlags = bind,
            CpuAccessFlags = cpu,
        };

        Check(CreateTexture2D(_device, ref description, out var texture), $"CreateTexture2D ({width}x{height}, format {format})");
        return texture;
    }

    private static IntPtr FirstAdapterWithAnOutput()
    {
        var iid = IidFactory1;
        Check(CreateDXGIFactory1(ref iid, out var factory), "CreateDXGIFactory1");

        try
        {
            for (var index = 0u; EnumAdapters1(factory, index, out var adapter) >= 0; index++)
            {
                if (EnumOutputs(adapter, 0, out var output) >= 0)
                {
                    Release(ref output);
                    return adapter;
                }

                Release(ref adapter);
            }
        }
        finally
        {
            Release(ref factory);
        }

        throw new InvalidOperationException("No adapter drives a display.");
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{what} returned 0x{hr:X8}.");
        }
    }

    public void Dispose()
    {
        Release(ref _outputView);
        Release(ref _inputView);
        Release(ref _targetStaging);
        Release(ref _target);
        Release(ref _sourceStaging);
        Release(ref _source);
        Release(ref _processor);
        Release(ref _enumerator);
        Release(ref _videoContext);
        Release(ref _videoDevice);
        Release(ref _context);
        Release(ref _device);
    }
}
