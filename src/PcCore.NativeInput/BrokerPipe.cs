using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcCore.NativeInput;

/// <summary>
/// Creates the broker pipe, and lets each side check who is on the other end of it.
/// <para>
/// <b>Why the pipe is created by hand rather than by <c>NamedPipeServerStream</c>.</b> The broker
/// runs at high integrity, and every kernel object a high-integrity process creates is labelled high
/// integrity with a no-write-up policy — which would make the pipe unwritable by PC Core, a medium
/// integrity process. The fix is to give the pipe an explicit medium-integrity label, and .NET's
/// pipe API has no way to express one. So the security descriptor is built from SDDL and the pipe
/// is created directly, which also makes the access rules something you can read in one line
/// instead of infer.
/// </para>
/// <para>
/// <b>Both ends verify each other.</b> The pipe's DACL admits only the owner's own SID, but "the
/// owner's own SID" is also what any program the owner runs has. So the broker additionally checks
/// which executable connected to it, and PC Core checks which executable it connected <em>to</em> —
/// the second is not paranoia, because a program that squatted the pipe name first would otherwise
/// receive everything the owner typed from their phone.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrokerPipe
{
    private const string Advapi32 = "advapi32.dll";
    private const string Kernel32 = "kernel32.dll";

    /// <summary>
    /// Owner gets full access; SYSTEM gets full access; everyone else gets nothing. The mandatory
    /// label is medium with no-write-up, which is what lets PC Core — an ordinary medium-integrity
    /// process — write to a pipe created by a high-integrity one.
    /// </summary>
    private static string SecurityDescriptor(string ownerSid) =>
        $"D:(A;;GA;;;{ownerSid})(A;;GA;;;SY)S:(ML;;NW;;;ME)";

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;

    /// <summary>
    /// One instance, deliberately. The broker serves exactly one client at a time, so a second
    /// program cannot quietly attach alongside PC Core and share the elevated input path.
    /// </summary>
    private const uint MaxInstances = 1;

    private const int BufferBytes = 64 * 1024;

    /// <summary>Creates the listening pipe. The caller wraps the handle in a NamedPipeServerStream.</summary>
    public static SafePipeHandle CreateServer(string ownerSid)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                SecurityDescriptor(ownerSid), SddlRevision1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The broker pipe security descriptor is invalid.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor.DangerousGetHandle(),
                InheritHandle = false,
            };

            var handle = CreateNamedPipeW(
                $@"\\.\pipe\{BrokerProtocol.PipeName(ownerSid)}",
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait,
                MaxInstances,
                BufferBytes,
                BufferBytes,
                0,
                ref attributes);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "The broker pipe could not be created.");
            }

            return handle;
        }
        finally
        {
            descriptor.Dispose();
        }
    }

    /// <summary>The full path of the process at the other end of a connected server pipe.</summary>
    public static string? ClientImagePath(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var processId) ? ImagePath(processId) : null;

    /// <summary>The full path of the process serving a connected client pipe.</summary>
    public static string? ServerImagePath(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var processId) ? ImagePath(processId) : null;

    /// <summary>
    /// Whether two executable paths are the same file, compared the way Windows compares paths.
    /// <para>
    /// Case-insensitive and fully resolved, because "the same executable" must not depend on how
    /// either side happened to spell the path.
    /// </para>
    /// </summary>
    public static bool SameImage(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? ImagePath(uint processId)
    {
        // Query-limited-information is enough for the image path and is the least this can ask for.
        // It also works across integrity levels in both directions for processes of the same user,
        // which is what lets a medium-integrity PC Core identify a high-integrity broker.
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (process.IsInvalid)
        {
            process.Dispose();
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;

            return QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SddlRevision1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport(Advapi32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl,
        uint revision,
        out LocalAllocHandle descriptor,
        out uint size);

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeOut,
        ref SecurityAttributes attributes);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        StringBuilder name,
        ref int size);

    private sealed class LocalAllocHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => LocalFree(handle) == nint.Zero;

        [DllImport(Kernel32)]
        private static extern nint LocalFree(nint memory);
    }
}
