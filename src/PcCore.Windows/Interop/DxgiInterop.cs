using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// The DXGI and Direct3D 11 surface Desktop Duplication needs, and nothing else.
/// <para>
/// <b>Why function pointers rather than <c>[ComImport]</c> interfaces.</b> A COM interface
/// declaration has to list every method of every base interface in order, because the runtime finds
/// a method by counting slots. Reaching <c>ID3D11DeviceContext::CopyResource</c> that way means
/// declaring forty methods nobody calls, and a single miscount is an access violation rather than a
/// compile error. Calling through the vtable directly makes the slot number the visible thing, so
/// each one can be stated, commented and checked against the header it comes from.
/// </para>
/// <para>
/// Every slot below was validated against a real adapter before any of it was used: a standalone
/// probe enumerated outputs, duplicated one, copied a frame to a staging texture, read the pixels
/// and released everything cleanly.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DxgiInterop
{
    // --- HRESULTs worth naming -----------------------------------------------------------------

    /// <summary>Nothing changed within the timeout. The commonest outcome on a still desktop.</summary>
    public const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

    /// <summary>The duplication is no longer valid: mode change, display recreation, driver reset.</summary>
    public const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    /// <summary>Another process holds the duplication, or the desktop is not accessible right now.</summary>
    public const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);

    /// <summary>The session is on a secure desktop (lock screen, UAC prompt) and cannot be read.</summary>
    public const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);

    public const int DxgiErrorAccessDenied = unchecked((int)0x887A002B);

    public const int DxgiErrorUnsupported = unchecked((int)0x887A0004);

    public const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);

    // --- Constants ------------------------------------------------------------------------------

    public const uint DxgiFormatB8G8R8A8Unorm = 87;
    public const uint D3D11UsageStaging = 3;
    public const uint D3D11CpuAccessRead = 0x20000;
    public const uint D3D11MapRead = 1;
    public const uint D3D11SdkVersion = 7;

    /// <summary>Must be UNKNOWN when an adapter is supplied, or device creation fails.</summary>
    public const int D3DDriverTypeUnknown = 0;

    public static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    // --- IUnknown -------------------------------------------------------------------------------

    /// <summary>IUnknown slot 0.</summary>
    public static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr result)
    {
        fixed (Guid* guid = &iid)
        fixed (IntPtr* target = &result)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)obj)[0])(obj, guid, target);
        }
    }

    /// <summary>IUnknown slot 2. Safe to call with a null pointer, which simplifies cleanup paths.</summary>
    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero)
        {
            return;
        }

        var target = obj;
        obj = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)target)[2])(target);
    }

    // --- IDXGIFactory1 (IDXGIObject 0-6, IDXGIFactory 7-11, IDXGIFactory1 12-13) ------------------

    /// <summary>IDXGIFactory1::EnumAdapters1, slot 12.</summary>
    public static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
    {
        fixed (IntPtr* target = &adapter)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[12])(factory, index, target);
        }
    }

    // --- IDXGIAdapter (IDXGIObject 0-6, then 7-9) --------------------------------------------------

    /// <summary>IDXGIAdapter::EnumOutputs, slot 7.</summary>
    public static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        fixed (IntPtr* target = &output)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)adapter)[7])(adapter, index, target);
        }
    }

    // --- IDXGIOutput (IDXGIObject 0-6, then 7-18) / IDXGIOutput1 (19-22) --------------------------

    /// <summary>IDXGIOutput::GetDesc, slot 7.</summary>
    public static int GetOutputDesc(IntPtr output, out DxgiOutputDesc desc)
    {
        fixed (DxgiOutputDesc* target = &desc)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, DxgiOutputDesc*, int>)(*(void***)output)[7])(output, target);
        }
    }

    /// <summary>IDXGIOutput1::DuplicateOutput, slot 22.</summary>
    public static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        fixed (IntPtr* target = &duplication)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(void***)output1)[22])(output1, device, target);
        }
    }

    // --- IDXGIOutputDuplication (IDXGIObject 0-6, then 7-14) ---------------------------------------

    /// <summary>IDXGIOutputDuplication::GetDesc, slot 7. Returns void.</summary>
    public static void GetDuplicationDesc(IntPtr duplication, out DxgiOutduplDesc desc)
    {
        fixed (DxgiOutduplDesc* target = &desc)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, DxgiOutduplDesc*, void>)(*(void***)duplication)[7])(duplication, target);
        }
    }

    /// <summary>IDXGIOutputDuplication::AcquireNextFrame, slot 8.</summary>
    public static int AcquireNextFrame(IntPtr duplication, uint timeoutMs, out DxgiOutduplFrameInfo info, out IntPtr resource)
    {
        fixed (DxgiOutduplFrameInfo* frameInfo = &info)
        fixed (IntPtr* target = &resource)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, DxgiOutduplFrameInfo*, IntPtr*, int>)
                (*(void***)duplication)[8])(duplication, timeoutMs, frameInfo, target);
        }
    }

    /// <summary>
    /// IDXGIOutputDuplication::GetFramePointerShape, slot 11.
    /// <para>
    /// Slots 9 and 10 are GetFrameDirtyRects and GetFrameMoveRects, which Nexus does not use — the
    /// whole frame is re-encoded every time. They are named here only so the count to 11 can be
    /// checked against dxgi1_2.h rather than trusted.
    /// </para>
    /// </summary>
    public static int GetFramePointerShape(
        IntPtr duplication,
        uint bufferSize,
        byte[] buffer,
        out uint required,
        out DxgiOutduplPointerShapeInfo info)
    {
        fixed (byte* target = buffer)
        fixed (uint* neededBytes = &required)
        fixed (DxgiOutduplPointerShapeInfo* shape = &info)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, uint*, DxgiOutduplPointerShapeInfo*, int>)
                (*(void***)duplication)[11])(duplication, bufferSize, target, neededBytes, shape);
        }
    }

    /// <summary>IDXGIOutputDuplication::ReleaseFrame, slot 14.</summary>
    public static int ReleaseFrame(IntPtr duplication) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)duplication)[14])(duplication);

    // --- ID3D11Device (IUnknown 0-2, then 3 CreateBuffer, 4 CreateTexture1D, 5 CreateTexture2D) ----

    /// <summary>ID3D11Device::CreateTexture2D, slot 5.</summary>
    public static int CreateTexture2D(IntPtr device, ref D3D11Texture2DDesc desc, out IntPtr texture)
    {
        fixed (D3D11Texture2DDesc* description = &desc)
        fixed (IntPtr* target = &texture)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11Texture2DDesc*, IntPtr, IntPtr*, int>)
                (*(void***)device)[5])(device, description, IntPtr.Zero, target);
        }
    }

    // --- ID3D11DeviceContext ----------------------------------------------------------------------
    // IUnknown 0-2, ID3D11DeviceChild 3-6, then the context's own methods from 7.
    // 7 VSSetConstantBuffers, 8 PSSetShaderResources, 9 PSSetShader, 10 PSSetSamplers,
    // 11 VSSetShader, 12 DrawIndexed, 13 Draw, 14 Map, 15 Unmap, ... 47 CopyResource.

    /// <summary>ID3D11DeviceContext::Map, slot 14.</summary>
    public static int Map(IntPtr context, IntPtr resource, out D3D11MappedSubresource mapped)
    {
        fixed (D3D11MappedSubresource* target = &mapped)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11MappedSubresource*, int>)
                (*(void***)context)[14])(context, resource, 0, D3D11MapRead, 0, target);
        }
    }

    /// <summary>ID3D11DeviceContext::Unmap, slot 15.</summary>
    public static void Unmap(IntPtr context, IntPtr resource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)(*(void***)context)[15])(context, resource, 0);

    /// <summary>ID3D11DeviceContext::CopyResource, slot 47.</summary>
    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)(*(void***)context)[47])(context, destination, source);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiOutputDesc
{
    public fixed char DeviceName[32];
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;

    public readonly string Name
    {
        get
        {
            fixed (char* name = DeviceName)
            {
                return new string(name, 0, 32).TrimEnd('\0');
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplDesc
{
    public uint Width;
    public uint Height;
    public uint RefreshRateNumerator;
    public uint RefreshRateDenominator;
    public uint Format;
    public uint ScanlineOrdering;
    public uint Scaling;
    public uint Rotation;

    /// <summary>True on integrated-only systems, where the desktop can be mapped without a copy.</summary>
    public int DesktopImageInSystemMemory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplFrameInfo
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;

    /// <summary>
    /// Zero means the desktop image did not change — the acquire returned only pointer news, and the
    /// texture contents are not a frame worth encoding.
    /// </summary>
    public uint AccumulatedFrames;

    public int RectsCoalesced;

    /// <summary>Protected content was blacked out. The capture is valid; parts of it are just black.</summary>
    public int ProtectedContentMaskedOut;

    public int PointerX;
    public int PointerY;
    public int PointerVisible;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

/// <summary>
/// DXGI_OUTDUPL_POINTER_SHAPE_INFO. <see cref="Type"/> is one of the DXGI_OUTDUPL_POINTER_SHAPE_TYPE
/// values: 1 monochrome, 2 colour, 4 masked colour.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DxgiOutduplPointerShapeInfo
{
    public uint Type;
    public uint Width;

    /// <summary>For a monochrome shape this is twice the visible height: an AND mask above an XOR mask.</summary>
    public uint Height;

    public uint Pitch;
    public int HotSpotX;
    public int HotSpotY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11MappedSubresource
{
    public IntPtr Data;
    public uint RowPitch;
    public uint DepthPitch;
}
