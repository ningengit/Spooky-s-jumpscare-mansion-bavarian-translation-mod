using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SJSMHDBavarianPatcher;

// source-generated JSON, with comment skipping enabled so // comments in JSON files work!
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ManifestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Keeps one backup of every game file the first time it is changed, in "_modbackup" inside the *_Data folder.
/// Re-running the patcher always starts again from the original, so patches never stack.
/// If the game updates (a file no longer matches what we wrote), the new file counts as the new original.
/// Safe to use from several threads.
/// </summary>
public sealed class BackupStore
{
    private readonly object gate = new();
    private readonly string dataDir;
    private readonly string backupDir;
    private readonly string manifestPath;
    private readonly Dictionary<string, string> patched; // relative path ('/') -> SHA-256 of the file as we left it
    private readonly HashSet<string> prepared = new(StringComparer.OrdinalIgnoreCase);

    public BackupStore(string dataDir)
    {
        this.dataDir = dataDir;
        backupDir = Path.Combine(dataDir, "_modbackup");
        manifestPath = Path.Combine(backupDir, "manifest.json");

        patched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(manifestPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), ManifestJsonContext.Default.DictionaryStringString);
                if (loaded != null)
                    foreach (var kv in loaded) patched[kv.Key.Replace('\\', '/')] = kv.Value;
            }
            catch { /* unreadable manifest: treat everything as unpatched */ }
        }
    }

    private string Rel(string fullPath) => Path.GetRelativePath(dataDir, fullPath).Replace('\\', '/');

    /// <summary>Makes sure the file on disk is the ORIGINAL one (restoring our backup if we patched it before).</summary>
    public void PrepareOriginal(string fullPath)
    {
        lock (gate)
        {
            string rel = Rel(fullPath);
            if (!prepared.Add(rel)) return;

            string backup = Path.Combine(backupDir, rel);
            if (File.Exists(backup) && patched.TryGetValue(rel, out string? hash) && Hash(fullPath) == hash)
            {
                File.Copy(backup, fullPath, true);
                Log.Line($"  restored original of {rel}");
            }
            else
            {
                // first run, or the game replaced the file: the current file is the original
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(fullPath, backup, true);
                patched.Remove(rel);
                Log.Line($"  backed up {rel}");
            }
        }
    }

    public void RecordPatched(string fullPath)
    {
        string hash = Hash(fullPath);
        lock (gate) patched[Rel(fullPath)] = hash;
    }

    public void Save()
    {
        lock (gate)
        {
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(patched, ManifestJsonContext.Default.DictionaryStringString));
        }
    }

    /// <summary>Puts every file we patched back to its original.</summary>
    public int Revert()
    {
        lock (gate)
        {
            int restored = 0;
            foreach (var (rel, hash) in patched.ToList())
            {
                string full = Path.Combine(dataDir, rel);
                string backup = Path.Combine(backupDir, rel);

                if (!File.Exists(backup))
                {
                    Log.Line($"  no backup for {rel}, skipped");
                }
                else if (File.Exists(full) && Hash(full) == hash)
                {
                    File.Copy(backup, full, true);
                    Log.Line($"  restored {rel}");
                    restored++;
                }
                else
                {
                    Log.Line($"  {rel} was changed after patching (game update?), left alone");
                }
                patched.Remove(rel);
            }

            Directory.CreateDirectory(backupDir);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(patched, ManifestJsonContext.Default.DictionaryStringString));
            return restored;
        }
    }

    private static string Hash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
