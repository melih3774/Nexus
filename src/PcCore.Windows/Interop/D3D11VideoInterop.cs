using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// The Direct3D 11 video processor, by vtable slot. See <see cref="DxgiInterop"/> for why slots.
/// <para>
/// Every slot and layout here was read from the C vtables and structures in <c>d3d11.h</c> of the
/// Windows 10.0.26100 SDK.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class D3D11VideoInterop
{
    public const uint DeviceCreateBgraSupport = 0x20;
    public const uint DeviceCreateVideoSupport = 0x800;

    public const uint DxgiFormatNv12 = 103;

    public const uint UsageDefault = 0;
    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;

    public const uint FrameFormatProgressive = 0;

    /// <summary>D3D11_VIDEO_USAGE_OPTIMAL_SPEED.</summary>
    public const uint VideoUsageOptimalSpeed = 1;

    /// <summary>D3D11_VIDEO_USAGE_OPTIMAL_QUALITY.</summary>
    public const uint VideoUsageOptimalQuality = 2;

    public const uint ViewDimensionTexture2D = 1;

    public static readonly Guid IidVideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
    public static readonly Guid IidVideoContext = new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4");

    // --- ID3D11VideoDevice ----------------------------------------------------------------------

    /// <summary>ID3D11VideoDevice::CreateVideoProcessor, slot 4.</summary>
    public static int CreateVideoProcessor(IntPtr videoDevice, IntPtr enumerator, uint rateIndex, out IntPtr processor)
    {
        fixed (IntPtr* target = &processor)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)
                (*(void***)videoDevice)[4])(videoDevice, enumerator, rateIndex, target);
        }
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorInputView, slot 8.</summary>
    public static int CreateInputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator, out IntPtr view)
    {
        var description = new VideoProcessorInputViewDesc { ViewDimension = ViewDimensionTexture2D };

        fixed (IntPtr* target = &view)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VideoProcessorInputViewDesc*, IntPtr*, int>)
                (*(void***)videoDevice)[8])(videoDevice, texture, enumerator, &description, target);
        }
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorOutputView, slot 9.</summary>
    public static int CreateOutputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator, out IntPtr view)
    {
        var description = new VideoProcessorOutputViewDesc { ViewDimension = ViewDimensionTexture2D };

        fixed (IntPtr* target = &view)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VideoProcessorOutputViewDesc*, IntPtr*, int>)
                (*(void***)videoDevice)[9])(videoDevice, texture, enumerator, &description, target);
        }
    }

    /// <summary>ID3D11VideoDevice::CreateVideoProcessorEnumerator, slot 10.</summary>
    public static int CreateEnumerator(IntPtr videoDevice, ref VideoProcessorContentDesc description, out IntPtr enumerator)
    {
        fixed (VideoProcessorContentDesc* desc = &description)
        fixed (IntPtr* target = &enumerator)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, VideoProcessorContentDesc*, IntPtr*, int>)
                (*(void***)videoDevice)[10])(videoDevice, desc, target);
        }
    }

    // --- ID3D11VideoContext ---------------------------------------------------------------------

    /// <summary>ID3D11VideoContext::VideoProcessorSetOutputColorSpace, slot 15. Returns void.</summary>
    public static void SetOutputColorSpace(IntPtr videoContext, IntPtr processor, uint colorSpace) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, void>)
            (*(void***)videoContext)[15])(videoContext, processor, &colorSpace);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamFrameFormat, slot 27. Returns void.</summary>
    public static void SetStreamFrameFormat(IntPtr videoContext, IntPtr processor, uint stream, uint format) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, void>)
            (*(void***)videoContext)[27])(videoContext, processor, stream, format);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamColorSpace, slot 28. Returns void.</summary>
    public static void SetStreamColorSpace(IntPtr videoContext, IntPtr processor, uint stream, uint colorSpace) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, void>)
            (*(void***)videoContext)[28])(videoContext, processor, stream, &colorSpace);

    /// <summary>ID3D11VideoContext::VideoProcessorSetStreamAutoProcessingMode, slot 37. Returns void.</summary>
    public static void SetStreamAutoProcessing(IntPtr videoContext, IntPtr processor, uint stream, bool enabled) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)
            (*(void***)videoContext)[37])(videoContext, processor, stream, enabled ? 1 : 0);

    /// <summary>ID3D11VideoContext::VideoProcessorBlt, slot 53, with a single progressive stream.</summary>
    public static int Blt(IntPtr videoContext, IntPtr processor, IntPtr outputView, IntPtr inputView)
    {
        var stream = new VideoProcessorStream { Enable = 1, InputSurface = inputView };

        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, VideoProcessorStream*, int>)
            (*(void***)videoContext)[53])(videoContext, processor, outputView, 0, 1, &stream);
    }

    // --- ID3D11Device and ID3D11DeviceContext, beyond what capture needs ------------------------

    /// <summary>ID3D11DeviceContext::UpdateSubresource, slot 48, for a whole subresource.</summary>
    public static void UpdateSubresource(IntPtr context, IntPtr texture, byte* source, uint rowPitch) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, byte*, uint, uint, void>)
            (*(void***)context)[48])(context, texture, 0, IntPtr.Zero, source, rowPitch, 0);

    // --- IDXGIAdapter ---------------------------------------------------------------------------

    /// <summary>IDXGIAdapter::GetDesc, slot 8. Only the description string is read back.</summary>
    public static string AdapterName(IntPtr adapter)
    {
        // DXGI_ADAPTER_DESC opens with WCHAR Description[128] and is under 320 bytes on x64.
        var description = stackalloc char[256];

        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)(*(void***)adapter)[8])(adapter, description);

        return hr < 0 ? "unknown adapter" : new string(description);
    }
}

/// <summary>D3D11_VIDEO_PROCESSOR_CONTENT_DESC.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorContentDesc
{
    public uint InputFrameFormat;
    public uint InputRateNumerator;
    public uint InputRateDenominator;
    public uint InputWidth;
    public uint InputHeight;
    public uint OutputRateNumerator;
    public uint OutputRateDenominator;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint Usage;
}

/// <summary>D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC, with the D3D11_TEX2D_VPIV arm of its union.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorInputViewDesc
{
    public uint FourCc;
    public uint ViewDimension;
    public uint MipSlice;
    public uint ArraySlice;
}

/// <summary>D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC. The union's largest arm is three UINTs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorOutputViewDesc
{
    public uint ViewDimension;
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

/// <summary>D3D11_VIDEO_PROCESSOR_STREAM.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoProcessorStream
{
    public int Enable;
    public uint OutputIndex;
    public uint InputFrameOrField;
    public uint PastFrames;
    public uint FutureFrames;
    public IntPtr PastSurfaces;
    public IntPtr InputSurface;
    public IntPtr FutureSurfaces;
    public IntPtr PastSurfacesRight;
    public IntPtr InputSurfaceRight;
    public IntPtr FutureSurfacesRight;
}

