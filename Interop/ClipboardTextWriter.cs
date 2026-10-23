using System.Runtime.InteropServices;

namespace alpha_island.Interop;

internal static class ClipboardTextWriter
{
    internal static void Copy(string text, Action<string>? write = null, Func<string?>? read = null)
    {
        try { (write ?? System.Windows.Clipboard.SetText)(text); }
        catch (ExternalException)
        {
            // OLE can report a flush failure after the requested text was committed.
            try { if (string.Equals((read ?? ClipboardTextReader.ReadText)(), text, StringComparison.Ordinal)) return; }
            catch (Exception error) when (error is ExternalException or InvalidOperationException) { }
            throw;
        }
    }
}
