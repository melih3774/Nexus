using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcCore.Windows.Interop;

/// <summary>
/// The Media Foundation surface a low-latency H.264 encoder needs, and nothing else.
/// <para>
/// <b>Why vtable calls rather than <c>[ComImport]</c>.</b> The same reason as
/// <see cref="DxgiInterop"/>: an interface declaration has to list every method of every base in
/// order, and <c>IMFMediaType</c> sits behind the thirty methods of <c>IMFAttributes</c>. Declaring
/// those thirty to reach <c>SetGUID</c> is thirty chances to be off by one, and being off by one
/// here is an access violation rather than a compile error. Numbering the slots explicitly makes
/// each one a claim that can be checked against the header it came from.
/// </para>
/// <para>
/// Every slot below was exercised against both encoders on this machine — the NVIDIA hardware MFT
/// and Microsoft's software fallback — by <c>--selftest-video</c> before any of it carried a frame.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class MediaFoundationInterop
{
    // --- Versions and startup -------------------------------------------------------------------

    /// <summary>(MF_SDK_VERSION shifted left 16) or MF_API_VERSION. Anything else fails startup.</summary>
    public const uint MfVersion = 0x00020070;

    /// <summary>Skips the sockets layer, which an encoder never touches.</summary>
    public const uint MfStartupNoSocket = 1;

    // --- HRESULTs worth naming ------------------------------------------------------------------

    public const int SOk = 0;

    /// <summary>The transform has nothing to give yet. The ordinary answer while a pipeline fills.</summary>
    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);

    /// <summary>The output type changed underneath us; re-read it and carry on.</summary>
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    /// <summary>No event is queued right now. Only seen with MF_EVENT_FLAG_NO_WAIT.</summary>
    public const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);

    public const int MfEInvalidMediaType = unchecked((int)0xC00D36B4);

    public const int MfEShutdown = unchecked((int)0xC00D3E85);

    public const int MfEAttributeNotFound = unchecked((int)0xC00D36E6);

    public const int RpcEChangedMode = unchecked((int)0x80010106);

    // --- Enumeration ----------------------------------------------------------------------------

    public const uint MftEnumFlagSyncMft = 0x00000001;
    public const uint MftEnumFlagAsyncMft = 0x00000002;
    public const uint MftEnumFlagHardware = 0x00000004;
    public const uint MftEnumFlagSortAndFilter = 0x00000040;

    /// <summary>
    /// MFT_CATEGORY_VIDEO_ENCODER from mftransform.h.
    /// <para>
    /// Worth naming carefully: the neighbouring categories in that header have the same shape and
    /// enumerating the wrong one is not an error. The first version of this asked for the
    /// demultiplexer category, got a successful call and an empty list, and reported that a machine
    /// with two hardware encoders in it had none.
    /// </para>
    /// </summary>
    public static readonly Guid MftCategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    // --- Media type GUIDs -----------------------------------------------------------------------

    public static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");

    public static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MfMtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    /// <summary>Bytes between the start of one row and the next. Positive means top-down.</summary>
    public static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// <summary>Which YUV matrix the samples use. 1 is BT.601, 2 is BT.709.</summary>
    public static readonly Guid MfMtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");

    /// <summary>Luma range. 2 is the limited 16-235 range every phone decoder assumes.</summary>
    public static readonly Guid MfMtVideoNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    public const uint YuvMatrixBt601 = 1;
    public const uint YuvMatrixBt709 = 2;
    public const uint NominalRange16To235 = 2;

    /// <summary>SPS and PPS as a blob on the output type. Not every encoder publishes it.</summary>
    public static readonly Guid MfMtMpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    public const uint InterlaceModeProgressive = 2;

    /// <summary>eAVEncH264VProfile_Main. Baseline costs quality; High is not universally decodable.</summary>
    public const uint AvcProfileMain = 77;

    // --- Transform attributes -------------------------------------------------------------------

    public static readonly Guid MfTransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MfTransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MftFriendlyNameAttribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    /// <summary>Also CODECAPI_AVLowLatencyMode. One GUID, two spellings in the SDK.</summary>
    public static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>Set on an output sample when it is an IDR — how a keyframe is recognised.</summary>
    public static readonly Guid MfSampleExtensionCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    // --- ICodecAPI ------------------------------------------------------------------------------

    public static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid CodecApiRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CodecApiMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CodecApiMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid CodecApiQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CodecApiGopSize = new("95152910-855b-4a1c-a035-9d6d6dc0ac0e");
    public static readonly Guid CodecApiBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04bab74c");
    public static readonly Guid CodecApiLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    public static readonly Guid CodecApiForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid CodecApiRealTime = new("143a0ff6-a131-43da-b81e-98fbb8ec378e");

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const uint RateControlCbr = 0;

    /// <summary>eAVEncCommonRateControlMode_PeakConstrainedVBR. A mean with a ceiling.</summary>
    public const uint RateControlPeakConstrainedVbr = 2;

    /// <summary>eAVEncCommonRateControlMode_LowDelayVBR. Preferred when the encoder offers it.</summary>
    public const uint RateControlLowDelayVbr = 4;

    // --- ProcessMessage -------------------------------------------------------------------------

    public const uint MftMessageCommandFlush = 0;
    public const uint MftMessageCommandDrain = 1;
    public const uint MftMessageNotifyBeginStreaming = 0x10000000;
    public const uint MftMessageNotifyEndStreaming = 0x10000001;
    public const uint MftMessageNotifyEndOfStream = 0x10000002;
    public const uint MftMessageNotifyStartOfStream = 0x10000003;

    // --- Async transform events -----------------------------------------------------------------

    public const uint MeError = 1;
    public const uint MeTransformNeedInput = 601;
    public const uint MeTransformHaveOutput = 602;
    public const uint MeTransformDrainComplete = 603;

    public const uint MfEventFlagNoWait = 1;

    /// <summary>The MFT allocates output samples itself. Every hardware encoder does.</summary>
    public const uint MftOutputStreamProvidesSamples = 0x00000100;

    // --- Structures -----------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    /// <summary>
    /// MFT_OUTPUT_DATA_BUFFER. Laid out by hand because the two pointers force 8-byte alignment and
    /// the padding is load-bearing: a packed 24-byte struct reads <c>dwStatus</c> out of a pointer.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct MftOutputDataBuffer
    {
        [FieldOffset(0)] public uint StreamId;
        [FieldOffset(8)] public IntPtr Sample;
        [FieldOffset(16)] public uint Status;
        [FieldOffset(24)] public IntPtr Events;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    /// <summary>A VARIANT, only ever holding a 32-bit value. 24 bytes on x64, union at offset 8.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Value;

        public const ushort VtBool = 11;
        public const ushort VtUi4 = 19;

        public static Variant Ui4(uint value) => new() { Type = VtUi4, Value = value };

        /// <summary>VARIANT_TRUE is -1, not 1. A 1 here is read as false by some encoders.</summary>
        public static Variant Bool(bool value) => new() { Type = VtBool, Value = value ? 0xFFFFu : 0u };
    }

    // --- Exports --------------------------------------------------------------------------------

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(
        Guid category,
        uint flags,
        MftRegisterTypeInfo* inputType,
        MftRegisterTypeInfo* outputType,
        out IntPtr* activates,
        out uint count);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void CoTaskMemFree(IntPtr block);

    public const uint CoinitMultithreaded = 0;

    // --- IUnknown -------------------------------------------------------------------------------

    public static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr result)
    {
        fixed (Guid* guid = &iid)
        fixed (IntPtr* target = &result)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)obj)[0])(obj, guid, target);
        }
    }

    /// <summary>Safe with a null pointer, which every cleanup path relies on.</summary>
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

    // --- IMFAttributes (IUnknown 0-2, then 3-32) -------------------------------------------------

    /// <summary>IMFAttributes::GetUINT32, slot 7.</summary>
    public static int GetUInt32(IntPtr attributes, ref Guid key, out uint value)
    {
        fixed (Guid* guid = &key)
        fixed (uint* target = &value)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)(*(void***)attributes)[7])(
                attributes, guid, target);
        }
    }

    /// <summary>IMFAttributes::GetAllocatedString, slot 13. The string is CoTaskMem and must be freed.</summary>
    public static int GetAllocatedString(IntPtr attributes, ref Guid key, out IntPtr value, out uint length)
    {
        fixed (Guid* guid = &key)
        fixed (IntPtr* target = &value)
        fixed (uint* size = &length)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)(*(void***)attributes)[13])(
                attributes, guid, target, size);
        }
    }

    /// <summary>IMFAttributes::GetBlobSize, slot 14.</summary>
    public static int GetBlobSize(IntPtr attributes, ref Guid key, out uint size)
    {
        fixed (Guid* guid = &key)
        fixed (uint* target = &size)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)(*(void***)attributes)[14])(
                attributes, guid, target);
        }
    }

    /// <summary>IMFAttributes::GetBlob, slot 15.</summary>
    public static int GetBlob(IntPtr attributes, ref Guid key, byte* buffer, uint bufferSize, out uint written)
    {
        fixed (Guid* guid = &key)
        fixed (uint* target = &written)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, uint*, int>)(*(void***)attributes)[15])(
                attributes, guid, buffer, bufferSize, target);
        }
    }

    /// <summary>IMFAttributes::SetUINT32, slot 21.</summary>
    public static int SetUInt32(IntPtr attributes, ref Guid key, uint value)
    {
        fixed (Guid* guid = &key)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)(*(void***)attributes)[21])(
                attributes, guid, value);
        }
    }

    /// <summary>IMFAttributes::SetUINT64, slot 22.</summary>
    public static int SetUInt64(IntPtr attributes, ref Guid key, ulong value)
    {
        fixed (Guid* guid = &key)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)(*(void***)attributes)[22])(
                attributes, guid, value);
        }
    }

    /// <summary>IMFAttributes::SetGUID, slot 24.</summary>
    public static int SetGuid(IntPtr attributes, ref Guid key, ref Guid value)
    {
        fixed (Guid* guid = &key)
        fixed (Guid* target = &value)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)(*(void***)attributes)[24])(
                attributes, guid, target);
        }
    }

    // --- IMFActivate (IMFAttributes 0-32, then 33-35) --------------------------------------------

    /// <summary>IMFActivate::ActivateObject, slot 33.</summary>
    public static int ActivateObject(IntPtr activate, ref Guid iid, out IntPtr obj)
    {
        fixed (Guid* guid = &iid)
        fixed (IntPtr* target = &obj)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)activate)[33])(
                activate, guid, target);
        }
    }

    /// <summary>IMFActivate::ShutdownObject, slot 34.</summary>
    public static int ShutdownObject(IntPtr activate) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)activate)[34])(activate);

    // --- IMFTransform (IUnknown 0-2, then 3-25) --------------------------------------------------

    public static readonly Guid IidMfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    /// <summary>IMFTransform::GetOutputStreamInfo, slot 7.</summary>
    public static int GetOutputStreamInfo(IntPtr transform, uint streamId, out MftOutputStreamInfo info)
    {
        fixed (MftOutputStreamInfo* target = &info)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, MftOutputStreamInfo*, int>)(*(void***)transform)[7])(
                transform, streamId, target);
        }
    }

    /// <summary>IMFTransform::GetAttributes, slot 8.</summary>
    public static int GetTransformAttributes(IntPtr transform, out IntPtr attributes)
    {
        fixed (IntPtr* target = &attributes)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)transform)[8])(transform, target);
        }
    }

    /// <summary>IMFTransform::GetInputAvailableType, slot 13.</summary>
    public static int GetInputAvailableType(IntPtr transform, uint streamId, uint index, out IntPtr type)
    {
        fixed (IntPtr* target = &type)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)(*(void***)transform)[13])(
                transform, streamId, index, target);
        }
    }

    /// <summary>IMFTransform::GetOutputAvailableType, slot 14.</summary>
    public static int GetOutputAvailableType(IntPtr transform, uint streamId, uint index, out IntPtr type)
    {
        fixed (IntPtr* target = &type)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)(*(void***)transform)[14])(
                transform, streamId, index, target);
        }
    }

    /// <summary>IMFTransform::SetInputType, slot 15.</summary>
    public static int SetInputType(IntPtr transform, uint streamId, IntPtr type, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)(*(void***)transform)[15])(
            transform, streamId, type, flags);

    /// <summary>IMFTransform::SetOutputType, slot 16.</summary>
    public static int SetOutputType(IntPtr transform, uint streamId, IntPtr type, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)(*(void***)transform)[16])(
            transform, streamId, type, flags);

    /// <summary>IMFTransform::GetOutputCurrentType, slot 18.</summary>
    public static int GetOutputCurrentType(IntPtr transform, uint streamId, out IntPtr type)
    {
        fixed (IntPtr* target = &type)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)transform)[18])(
                transform, streamId, target);
        }
    }

    /// <summary>IMFTransform::ProcessMessage, slot 23.</summary>
    public static int ProcessMessage(IntPtr transform, uint message, IntPtr param) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)(*(void***)transform)[23])(transform, message, param);

    /// <summary>IMFTransform::ProcessInput, slot 24.</summary>
    public static int ProcessInput(IntPtr transform, uint streamId, IntPtr sample, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)(*(void***)transform)[24])(
            transform, streamId, sample, flags);

    /// <summary>IMFTransform::ProcessOutput, slot 25.</summary>
    public static int ProcessOutput(
        IntPtr transform, uint flags, uint bufferCount, MftOutputDataBuffer* buffers, out uint status)
    {
        fixed (uint* target = &status)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, MftOutputDataBuffer*, uint*, int>)
                (*(void***)transform)[25])(transform, flags, bufferCount, buffers, target);
        }
    }

    // --- IMFMediaEventGenerator (IUnknown 0-2, then 3-6) -----------------------------------------

    public static readonly Guid IidMfMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");

    /// <summary>IMFMediaEventGenerator::GetEvent, slot 3. Blocks unless MF_EVENT_FLAG_NO_WAIT.</summary>
    public static int GetEvent(IntPtr generator, uint flags, out IntPtr mediaEvent)
    {
        fixed (IntPtr* target = &mediaEvent)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)generator)[3])(
                generator, flags, target);
        }
    }

    /// <summary>
    /// IMFMediaEventGenerator::QueueEvent, slot 6.
    /// <para>
    /// The only way to wake a thread parked in a blocking <c>GetEvent</c> without tearing down the
    /// object it is blocked on. Shutdown posts one of these; the pump sees an event type it does not
    /// recognise, loops, notices it is stopping, and leaves — while every pointer it might touch is
    /// still valid.
    /// </para>
    /// </summary>
    public static int QueueEvent(IntPtr generator, uint type, ref Guid extendedType, int status, IntPtr value)
    {
        fixed (Guid* guid = &extendedType)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, int, IntPtr, int>)(*(void***)generator)[6])(
                generator, type, guid, status, value);
        }
    }

    // --- IMFMediaEvent (IMFAttributes 0-32, then 33-36) ------------------------------------------

    /// <summary>IMFMediaEvent::GetType, slot 33.</summary>
    public static int GetEventType(IntPtr mediaEvent, out uint type)
    {
        fixed (uint* target = &type)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)(*(void***)mediaEvent)[33])(mediaEvent, target);
        }
    }

    // --- IMFSample (IMFAttributes 0-32, then 33-46) ----------------------------------------------

    /// <summary>IMFSample::SetSampleTime, slot 36.</summary>
    public static int SetSampleTime(IntPtr sample, long time) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)(*(void***)sample)[36])(sample, time);

    /// <summary>IMFSample::SetSampleDuration, slot 38.</summary>
    public static int SetSampleDuration(IntPtr sample, long duration) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)(*(void***)sample)[38])(sample, duration);

    /// <summary>IMFSample::ConvertToContiguousBuffer, slot 41.</summary>
    public static int ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        fixed (IntPtr* target = &buffer)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)sample)[41])(sample, target);
        }
    }

    /// <summary>IMFSample::AddBuffer, slot 42.</summary>
    public static int AddBuffer(IntPtr sample, IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)sample)[42])(sample, buffer);

    // --- IMFMediaBuffer (IUnknown 0-2, then 3-7) -------------------------------------------------

    /// <summary>IMFMediaBuffer::Lock, slot 3.</summary>
    public static int LockBuffer(IntPtr buffer, out byte* data, out uint maxLength, out uint currentLength)
    {
        fixed (byte** target = &data)
        fixed (uint* max = &maxLength)
        fixed (uint* current = &currentLength)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)(*(void***)buffer)[3])(
                buffer, target, max, current);
        }
    }

    /// <summary>IMFMediaBuffer::Unlock, slot 4.</summary>
    public static int UnlockBuffer(IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)buffer)[4])(buffer);

    /// <summary>IMFMediaBuffer::SetCurrentLength, slot 6.</summary>
    public static int SetCurrentLength(IntPtr buffer, uint length) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)(*(void***)buffer)[6])(buffer, length);

    // --- ICodecAPI (IUnknown 0-2, then 3-...) ----------------------------------------------------

    /// <summary>ICodecAPI::IsSupported, slot 3.</summary>
    public static int IsSupported(IntPtr codec, ref Guid api)
    {
        fixed (Guid* guid = &api)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)(*(void***)codec)[3])(codec, guid);
        }
    }

    /// <summary>ICodecAPI::SetValue, slot 9.</summary>
    public static int SetCodecValue(IntPtr codec, ref Guid api, ref Variant value)
    {
        fixed (Guid* guid = &api)
        fixed (Variant* target = &value)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)(*(void***)codec)[9])(
                codec, guid, target);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------

    public static bool Succeeded(int hr) => hr >= 0;

    public static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
        }
    }

    /// <summary>Packs two halves the way MF_MT_FRAME_SIZE and MF_MT_FRAME_RATE expect.</summary>
    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;
}
