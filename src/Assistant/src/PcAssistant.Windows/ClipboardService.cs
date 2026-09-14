using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcAssistant.Windows;

public sealed record ClipboardInfo(string? Text, IReadOnlyList<string> Formats, bool TextTruncated);

public sealed class ClipboardService : IDisposable
{
    private readonly ApartmentWorker worker = new(ApartmentState.STA);

    public Task<ClipboardInfo> ReadAsync(int maxCharacters = 16000, CancellationToken cancellationToken = default)
    {
        if (maxCharacters is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        return worker.RunAsync(() =>
        {
            var data = Clipboard.GetDataObject();
            var formats = data?.GetFormats(autoConvert: false).Take(64).ToArray() ?? [];
            var text = data?.GetData(DataFormats.UnicodeText, autoConvert: false) as string;
            return new ClipboardInfo(text is null ? null : text[..Math.Min(text.Length, maxCharacters)], formats, text?.Length > maxCharacters);
        }, cancellationToken);
    }

    public Task<string> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 100000) throw new ArgumentOutOfRangeException(nameof(text), "Clipboard text is limited to 100,000 characters.");
        return worker.RunAsync(() =>
        {
            if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text, TextDataFormat.UnicodeText);
            return "Clipboard text replaced.";
        }, cancellationToken);
    }
    public void Dispose() => worker.Dispose();
}
