using Microsoft.JSInterop;
using PclPatcher.Core;

namespace PclPatcher.Web;

public static class BrowserApi
{
    private static byte[]? original;
    private static PatchOutput? output;

    [JSInvokable]
    public static Inspection Inspect(byte[] bytes)
    {
        Reset();
        var inspection = PatchService.Inspect(bytes);
        original = bytes;
        return inspection;
    }

    [JSInvokable]
    public static object Patch()
    {
        if (original == null) throw new InvalidOperationException("Choose an executable first.");
        output = null;
        output = PatchService.Patch(original);
        original = null;
        return new { output.Sha256, Size = output.Bytes.Length, output.Log };
    }

    [JSInvokable]
    public static byte[] Download() => output?.Bytes ?? throw new InvalidOperationException("No verified output is available.");

    [JSInvokable]
    public static void Reset()
    {
        original = null;
        output = null;
    }
}
