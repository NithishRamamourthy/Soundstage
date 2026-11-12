// Atomic file replacement: write to a sibling temp file, then move it over the
// target so a crash never leaves a half-written file behind.
using System;
using System.IO;

namespace Soundstage.UI;

public static class AtomicFile
{
    /// <summary>Writes text to a file atomically, creating the parent directory if needed.</summary>
    public static void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }   // never mask the original failure
            throw;
        }
    }
}
