using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

using PcCore.Capabilities.Clipboard;

namespace PcCore.Host;

/// <summary>
/// The Windows clipboard, through Windows Forms.
/// <para>
/// In the host rather than <c>PcCore.Windows</c> because Windows Forms is already here for the tray,
/// and its clipboard is the well-trodden path: OLE clipboard access on an STA thread, retries while
/// another program holds the clipboard open, and <c>copy: true</c> so the text stays on the clipboard
/// after the thread that put it there has gone. Doing the same with raw <c>OpenClipboard</c> needs a
/// window that keeps pumping messages for as long as Nexus owns the clipboard, or every other program
/// that empties it waits on Nexus.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsClipboard : IClipboardAccess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string? ReadText() => OnStaThread(static () =>
        Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null);

    public void WriteText(string text) => OnStaThread(() =>
    {
        Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true, retryTimes: 10, retryDelay: 50);
        return true;
    });

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Nexus clipboard",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(Timeout))
        {
            throw new IOException("The clipboard did not answer.");
        }

        return failure switch
        {
            null => result,

            // What Windows Forms throws when another program has the clipboard open, or when this
            // session cannot reach it (the lock screen, a disconnected session).
            ExternalException or ThreadStateException => throw new IOException("The clipboard could not be opened.", failure),

            _ => throw new IOException("The clipboard could not be used.", failure),
        };
    }
}
