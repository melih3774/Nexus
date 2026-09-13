using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Clipboard;
using PcCore.Protocol;
using Xunit;

namespace PcCore.Tests.Capabilities;

/// <summary>
/// The clipboard's two answers that stop text bouncing between the PC and the phone: where the text
/// came from, and whether setting it changed anything.
/// </summary>
public sealed class ClipboardTests
{
    private readonly FakeClipboard _windows = new();
    private readonly ClipboardService _clipboard;

    public ClipboardTests() => _clipboard = new ClipboardService(_windows);

    [Fact]
    public void TextCopiedOnThePcComesFromThePc()
    {
        _windows.Text = "copied on the PC";

        var content = _clipboard.Get(null);

        Assert.True(content.HasText);
        Assert.Equal("copied on the PC", content.Text);
        Assert.Equal(ClipboardService.OriginPc, content.Origin);
    }

    [Fact]
    public void TextTheSetPutThereIsRecognisedAsThePhonesOwn()
    {
        var set = _clipboard.Set("from the phone");

        Assert.True(set.Changed);
        Assert.Equal("from the phone", _windows.Text);

        var content = _clipboard.Get(null);

        // A phone asking for the PC's clipboard now gets its own text back, labelled as such, and the
        // version it was told when it sent it - so it can skip copying it to itself.
        Assert.Equal(ClipboardService.OriginPhone, content.Origin);
        Assert.Equal(set.Version, content.Version);
    }

    [Fact]
    public void SendingTextThePcAlreadyHasIsNotAChange()
    {
        _windows.Text = "same";
        var before = _clipboard.Get(null);

        var set = _clipboard.Set("same");

        Assert.False(set.Changed);
        Assert.Equal(0, _windows.Writes);
        Assert.Equal(before.Version, set.Version);

        // Nor did it relabel the PC's own text as the phone's.
        Assert.Equal(ClipboardService.OriginPc, _clipboard.Get(null).Origin);
    }

    [Fact]
    public void ABackAndForthSettlesInsteadOfLooping()
    {
        // Phone -> PC.
        var sent = _clipboard.Set("ping");

        // PC -> phone: the phone reads it, sees it is its own, and does not copy it.
        var read = _clipboard.Get(null);
        Assert.Equal(ClipboardService.OriginPhone, read.Origin);

        // Even a client that ignored that and sent it straight back changes nothing.
        var echoed = _clipboard.Set(read.Text);
        Assert.False(echoed.Changed);
        Assert.Equal(1, _windows.Writes);

        // Repeated reads of an unchanged clipboard are one version, not a stream of "new" text.
        Assert.Equal(sent.Version, _clipboard.Get(null).Version);
        Assert.Equal(sent.Version, _clipboard.Get(null).Version);
    }

    [Fact]
    public void CopyingSomethingElseOnThePcIsANewVersionFromThePc()
    {
        var sent = _clipboard.Set("from the phone");

        _windows.Text = "copied on the PC afterwards";
        var changed = _clipboard.Get(null);

        Assert.Equal(ClipboardService.OriginPc, changed.Origin);
        Assert.True(changed.Version > sent.Version);

        // And if the phone's old text is copied again on the PC, it came from the PC this time.
        _windows.Text = "from the phone";
        Assert.Equal(ClipboardService.OriginPc, _clipboard.Get(null).Origin);
    }

    [Fact]
    public void LongTextIsTruncatedOnTheWayOutAndRefusedOnTheWayIn()
    {
        _windows.Text = new string('x', 100);

        var content = _clipboard.Get(10);
        Assert.True(content.Truncated);
        Assert.Equal(10, content.Text!.Length);
        Assert.Equal(100, content.Length);

        var refused = Assert.Throws<CapabilityException>(() => _clipboard.Set(new string('y', ClipboardService.MaxTextChars + 1)));
        Assert.Equal(ErrorCodes.InvalidParameters, refused.Code);

        Assert.Equal(ErrorCodes.InvalidParameters, Assert.Throws<CapabilityException>(() => _clipboard.Set(string.Empty)).Code);
    }

    [Fact]
    public void AnEmptyClipboardHasNoText()
    {
        var content = _clipboard.Get(null);

        Assert.False(content.HasText);
        Assert.Null(content.Text);
    }

    [Fact]
    public void AClipboardAnotherProgramIsHoldingIsReportedAsBusy()
    {
        _windows.Busy = true;

        Assert.Equal(ErrorCodes.ClipboardUnavailable, Assert.Throws<CapabilityException>(() => _clipboard.Get(null)).Code);
        Assert.Equal(ErrorCodes.ClipboardUnavailable, Assert.Throws<CapabilityException>(() => _clipboard.Set("x")).Code);
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public bool Busy { get; set; }

        public int Writes { get; private set; }

        public string? ReadText() => Busy ? throw new IOException("busy") : Text;

        public void WriteText(string text)
        {
            if (Busy)
            {
                throw new IOException("busy");
            }

            Text = text;
            Writes++;
        }
    }
}
