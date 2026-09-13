using System.Security.Cryptography;
using System.Text;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Clipboard;

/// <summary>The PC's clipboard, as text. Implemented on the Windows side.</summary>
public interface IClipboardAccess
{
    /// <summary>The clipboard's text, or <see langword="null"/> when it holds none.</summary>
    /// <exception cref="IOException">The clipboard could not be opened.</exception>
    string? ReadText();

    /// <exception cref="IOException">The clipboard could not be opened.</exception>
    void WriteText(string text);
}

public sealed record GetClipboardParameters
{
    /// <summary>At most this many characters come back. Defaults to, and is capped at, <see cref="ClipboardService.MaxTextChars"/>.</summary>
    public int? MaxChars { get; init; }
}

public sealed record ClipboardContent
{
    public required bool HasText { get; init; }

    public string? Text { get; init; }

    /// <summary>The whole text's length, even when <see cref="Truncated"/>.</summary>
    public required int Length { get; init; }

    public bool Truncated { get; init; }

    /// <summary>
    /// Goes up each time Nexus sees different text on the clipboard. A client that remembers the last
    /// version it acted on can tell "still the same clipboard" from "changed", without comparing text.
    /// </summary>
    public required long Version { get; init; }

    /// <summary>
    /// <c>phone</c> while the clipboard still holds what the phone sent; <c>pc</c> once anything else
    /// has been copied. What a phone checks so it never copies its own text back to itself.
    /// </summary>
    public required string Origin { get; init; }
}

public sealed record SetClipboardParameters
{
    public string? Text { get; init; }
}

public sealed record SetClipboardResult(bool Changed, long Version, int Length);

/// <summary>
/// Text between the PC's clipboard and the phone, explicitly, with just enough memory to prevent echoes.
/// <para>
/// <b>The loop this prevents.</b> Anything that copies both ways - even two buttons pressed in the
/// wrong order - can bounce one piece of text back and forth: phone sends it, the PC now "has new
/// text", the phone copies that "new" text to itself, and round again. Two facts break it. Setting
/// text the clipboard already holds changes nothing, so it is not a new version. And the clipboard
/// says where its text came from, so a phone can recognise its own.
/// </para>
/// <para>
/// Only a hash of the text is remembered, in memory. Clipboards hold passwords; nothing here writes
/// one to disk or to a log.
/// </para>
/// </summary>
public sealed class ClipboardService(IClipboardAccess clipboard)
{
    /// <summary>
    /// A quarter of a million characters. Android refuses clipboard data much past a megabyte of
    /// UTF-16, and text longer than this is a file, which Files moves better.
    /// </summary>
    public const int MaxTextChars = 256 * 1024;

    public const string OriginPc = "pc";
    public const string OriginPhone = "phone";

    private readonly Lock _gate = new();

    private string? _seenHash;
    private string? _sentHash;
    private string _origin = OriginPc;
    private long _version;

    public ClipboardContent Get(int? maxChars)
    {
        var text = Read();
        var limit = Math.Clamp(maxChars ?? MaxTextChars, 1, MaxTextChars);

        lock (_gate)
        {
            Observe(text);

            return new ClipboardContent
            {
                HasText = text is not null,
                Text = text is null ? null : text.Length > limit ? text[..limit] : text,
                Length = text?.Length ?? 0,
                Truncated = text?.Length > limit,
                Version = _version,
                Origin = _origin,
            };
        }
    }

    public SetClipboardResult Set(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "There is no text to put on the clipboard.");
        }

        if (text.Length > MaxTextChars)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, $"Clipboard text is limited to {MaxTextChars} characters.");
        }

        var current = Read();

        lock (_gate)
        {
            if (string.Equals(current, text, StringComparison.Ordinal))
            {
                // Already there. Writing it again would be a new clipboard event for every clipboard
                // watcher on the PC, and a new version for this one, for no change at all.
                Observe(current);
                return new SetClipboardResult(false, _version, text.Length);
            }
        }

        try
        {
            clipboard.WriteText(text);
        }
        catch (IOException)
        {
            throw Unavailable();
        }

        lock (_gate)
        {
            _sentHash = Hash(text);
            Observe(text);
            return new SetClipboardResult(true, _version, text.Length);
        }
    }

    /// <summary>Called with <see cref="_gate"/> held.</summary>
    private void Observe(string? text)
    {
        var hash = text is null ? null : Hash(text);

        if (hash == _seenHash)
        {
            return;
        }

        _seenHash = hash;
        _version++;

        if (hash is not null && hash == _sentHash)
        {
            _origin = OriginPhone;
        }
        else
        {
            // Something else was copied. If the phone's text comes back later, it came back from the PC.
            _origin = OriginPc;
            _sentHash = null;
        }
    }

    private string? Read()
    {
        try
        {
            return clipboard.ReadText();
        }
        catch (IOException)
        {
            throw Unavailable();
        }
    }

    private static CapabilityException Unavailable() =>
        new(ErrorCodes.ClipboardUnavailable, "The PC's clipboard is busy. Another program is holding it; try again.");

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary><c>clipboard.get</c>: the PC's clipboard text.</summary>
public sealed class GetClipboardAction(ClipboardService clipboard) : CapabilityAction<GetClipboardParameters, ClipboardContent>
{
    public override string Name => "clipboard.get";

    public override string Description => "The text on the PC's clipboard, and whether it came from the phone.";

    protected override ValueTask<ClipboardContent> ExecuteAsync(GetClipboardParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(clipboard.Get(parameters.MaxChars));
}

/// <summary><c>clipboard.set</c>: put text on the PC's clipboard.</summary>
public sealed class SetClipboardAction(ClipboardService clipboard) : CapabilityAction<SetClipboardParameters, SetClipboardResult>
{
    public override string Name => "clipboard.set";

    public override string Description => "Put text on the PC's clipboard.";

    protected override ValueTask<SetClipboardResult> ExecuteAsync(SetClipboardParameters parameters, CapabilityContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(clipboard.Set(parameters.Text));
}
