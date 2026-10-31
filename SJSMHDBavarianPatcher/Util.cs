using System.Text;

namespace SJSMHDBavarianPatcher;

/// <summary>
/// Output of files that are patched in parallel is collected per thread and printed in one piece,
/// so lines of different files never get mixed up.
/// </summary>
internal static class Log
{
    private static readonly object ConsoleLock = new();
    [ThreadStatic] private static StringBuilder? buffer;

    public static void Begin() => buffer = new StringBuilder();

    /// <summary>Buffered while a file is being patched, printed immediately otherwise.</summary>
    public static void Line(string text)
    {
        if (buffer != null) buffer.AppendLine(text);
        else lock (ConsoleLock) Console.WriteLine(text);
    }

    /// <summary>Always printed immediately (progress of long operations).</summary>
    public static void Status(string text)
    {
        lock (ConsoleLock) Console.WriteLine(text);
    }

    public static void End()
    {
        if (buffer == null) return;
        string text = buffer.ToString();
        buffer = null;
        lock (ConsoleLock) Console.Write(text);
    }
}

internal static class PathUtil
{
    /// <summary>
    /// Combines paths and finds the real spelling of every part, because Linux file systems are case sensitive
    /// ("sharedassets0.assets" and "SharedAssets0.assets" are different files there).
    /// Both '/' and '\' are accepted as separators in <paramref name="relative"/>.
    /// </summary>
    public static string Combine(string root, string relative)
    {
        string current = root;
        foreach (string part in relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(current, part);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            string? found = null;
            if (Directory.Exists(current))
            {
                found = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            }
            current = found ?? candidate;
        }
        return Path.GetFullPath(current);
    }

    /// <summary>A file that ships next to the program (Audio/..., Textures/...), or an absolute path.</summary>
    public static string Local(string path)
    {
        string resolved = Path.IsPathRooted(path) ? path : Combine(AppContext.BaseDirectory, path);

        // If a .wav file was requested in ModList, automatically use the .ogg version if it exists!
        if (!File.Exists(resolved) && resolved.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            string oggCandidate = Path.ChangeExtension(resolved, ".ogg");
            if (File.Exists(oggCandidate)) return oggCandidate;
        }
        return resolved;
    }
}
