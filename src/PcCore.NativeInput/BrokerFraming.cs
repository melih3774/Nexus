using System.Buffers.Binary;
using System.Text.Json;

namespace PcCore.NativeInput;

/// <summary>
/// Length-prefixed JSON frames over the broker pipe.
/// <para>
/// A four-byte little-endian length then that many bytes of UTF-8. Both sides check the length
/// against <see cref="BrokerProtocol.MaxFrameBytes"/> <em>before</em> allocating anything, so a
/// malformed or hostile prefix cannot make either process reserve memory on request. Everything
/// else about the transport — who may connect, who is on the other end — is settled by the pipe's
/// own security descriptor and the broker's client check, not here.
/// </para>
/// </summary>
public static class BrokerFraming
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The blocking write, for the client.
    /// <para>
    /// PC Core's input path is synchronous from the capability layer down, and it is also the
    /// latency-sensitive half: a pointer move waits for this. A synchronous pipe opened in
    /// synchronous mode is measurably the cheapest way to do one round trip, and wrapping the async
    /// version would add a task and a context switch per mouse movement for no benefit.
    /// </para>
    /// </summary>
    public static void Write<T>(Stream stream, T message)
    {
        stream.Write(Frame(message));
        stream.Flush();
    }

    /// <summary>Reads one frame, blocking. Returns null at end of stream.</summary>
    public static T? Read<T>(Stream stream)
        where T : class
    {
        Span<byte> header = stackalloc byte[4];

        if (!ReadExactly(stream, header))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length <= 0 || length > BrokerProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"The broker frame length {length} is not within the protocol bounds.");
        }

        var payload = new byte[length];

        return ReadExactly(stream, payload) ? JsonSerializer.Deserialize<T>(payload, Options) : null;
    }

    private static byte[] Frame<T>(T message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);

        if (payload.Length > BrokerProtocol.MaxFrameBytes)
        {
            throw new InvalidOperationException("The broker frame is larger than the protocol allows.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        return frame;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);

        if (payload.Length > BrokerProtocol.MaxFrameBytes)
        {
            throw new InvalidOperationException("The broker frame is larger than the protocol allows.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Returns null at end of stream, which is an ordinary disconnect.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        var header = new byte[4];

        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length <= 0 || length > BrokerProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"The broker frame length {length} is not within the protocol bounds.");
        }

        var payload = new byte[length];

        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(payload, Options);
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
