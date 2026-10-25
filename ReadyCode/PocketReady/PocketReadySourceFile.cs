// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using System.Text;

namespace ReadyCode.PocketReady;

/// <summary>Stage a complete UTF-8 source beside its target before replacing it; cancellation never truncates the existing file.</summary>
public static class PocketReadySourceFile
{
    public static async Task SaveAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
