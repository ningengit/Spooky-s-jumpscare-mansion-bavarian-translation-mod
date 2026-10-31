using System.Security.Cryptography;
using System.Text;

namespace PlainAndEasyInjector;

public sealed partial class Patcher
{
    const string BackupFolderName = "_PlainAndEasyInjector_backup";

    // ------------------------------------------------------------------
    // Install: patches the game in place. The first time, the original of every file that gets replaced is copied to
    // <data folder>/_PlainAndEasyInjector_backup (Unity: SpookyUnity_Data/_PlainAndEasyInjector_backup).
    // Running it again always starts from those originals, so patches never stack. Files we add (PlainAndEasyInjector.dll, ...)
    // are remembered too, so Uninstall() can remove them. Returns false (and prints why) if it failed.
    // ------------------------------------------------------------------
    public bool Install()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PlainAndEasyInjector-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = SaveCore(temp, copyGameFiles: false);
            var plan = files.Select(f => (Staged: Path.Combine(temp, f), Dest: f == RuntimeInitFile ? Path.Combine(dataRoot, f) : Path.Combine(dllDir, f))).ToList();

            // fail before touching anything if the game is running
            foreach (var (_, dest) in plan.Where(p => File.Exists(p.Dest)))
                try { using (File.Open(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException ex) { throw new IOException($"{dest} is in use. Close the game and try again.", ex); }

            foreach (var (staged, dest) in plan)
            {
                install.BeforeOverwrite(dest);
                File.Copy(staged, dest, overwrite: true);
                install.Record(dest);
                Info($"installed {install.Relative(dest)}");
            }
            // files a previous install added or replaced that this one no longer writes (e.g. a DLL your hooks stopped using)
            install.UndoOthers(plan.Select(p => p.Dest));
            install.Save();
            Info($"Done. Backups of the original files are in {install.BackupDir}");
            return true;
        }
        catch (Exception ex)
        {
            install.Save();   // keep what was recorded so far, so Uninstall can still clean up
            Console.Error.WriteLine($"[PlainAndEasyInjector] Installing failed: {ex.Message}");
            Console.Error.WriteLine(ex);
            return false;
        }
        finally
        {
            if (Directory.Exists(temp)) try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// Puts back every original file that Install() replaced and removes the files it added.
    /// dllPath: the game DLL you passed to Open() (e.g. ...\SpookyUnity_Data\Managed\Assembly-CSharp.dll).
    /// Returns the number of files restored or removed.
    public static int Uninstall(string dllPath)
    {
        var dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dllPath))!)!;
        var state = InstallState.Load(dataRoot);
        int count = state.UndoAll();
        state.Save();
        return count;
    }

    /// Which file to read a game DLL from: the backed-up original if the file in the game is our patched copy.
    string ReadPathFor(string gamePath) => install.OriginalOf(gamePath);

    /// Remembers what Install() changed: "R" = replaced (an original is in the backup folder), "A" = added by us.
    /// Each entry stores the SHA-256 of the file as we left it: if the file changed since (game update), it's the new original.
    sealed class InstallState
    {
        readonly string root;
        readonly string manifest;
        readonly Dictionary<string, (char Kind, string Hash)> entries = new(StringComparer.OrdinalIgnoreCase);

        public string BackupDir { get; }

        InstallState(string root)
        {
            this.root = root;
            BackupDir = Path.Combine(root, BackupFolderName);
            manifest = Path.Combine(BackupDir, "manifest.txt");
        }

        public static InstallState Load(string root)
        {
            var s = new InstallState(root);
            if (File.Exists(s.manifest))
                foreach (var line in File.ReadAllLines(s.manifest))
                {
                    var parts = line.Split(new[] { '\t' }, 3);
                    if (parts.Length == 3 && parts[0].Length == 1) s.entries[parts[2]] = (parts[0][0], parts[1]);
                }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(BackupDir);
            File.WriteAllLines(manifest, entries.Select(e => $"{e.Value.Kind}\t{e.Value.Hash}\t{e.Key}"));
        }

        public string Relative(string full)
        {
            var rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
            return rel.TrimStart('\\', '/').Replace('\\', '/');
        }

        string Full(string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        string BackupOf(string rel) => Path.Combine(BackupDir, rel.Replace('/', Path.DirectorySeparatorChar));

        /// The file the game originally had: our backup if 'full' is still exactly what we installed, else 'full' itself.
        public string OriginalOf(string full)
        {
            var rel = Relative(full);
            return entries.TryGetValue(rel, out var e) && e.Kind == 'R' && File.Exists(BackupOf(rel)) && IsOurs(full, e.Hash)
                ? BackupOf(rel) : full;
        }

        static bool IsOurs(string full, string hash) => File.Exists(full) && Hash(full) == hash;

        /// Call before overwriting a file in the game: backs up the original the first time.
        public void BeforeOverwrite(string full)
        {
            var rel = Relative(full);
            if (entries.TryGetValue(rel, out var e) && IsOurs(full, e.Hash)) return;   // ours from last time: the backup (if any) is still right
            if (File.Exists(full))
            {
                // first install, or the game replaced the file (update): what's there now is the original
                var backup = BackupOf(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(full, backup, overwrite: true);
                entries[rel] = ('R', "");
            }
            else entries[rel] = (entries.TryGetValue(rel, out var old) && old.Kind == 'R' ? 'R' : 'A', "");   // missing: keep "restore" if it was ours to restore
        }

        public void Record(string full)
        {
            var rel = Relative(full);
            var kind = entries.TryGetValue(rel, out var e) ? e.Kind : 'A';
            entries[rel] = (kind, Hash(full));
        }

        public void UndoOthers(IEnumerable<string> keepFull)
        {
            var keep = new HashSet<string>(keepFull.Select(Relative), StringComparer.OrdinalIgnoreCase);
            foreach (var rel in entries.Keys.Where(k => !keep.Contains(k)).ToList()) Undo(rel);
        }

        public int UndoAll()
        {
            int count = 0;
            foreach (var rel in entries.Keys.ToList()) if (Undo(rel)) count++;
            return count;
        }

        bool Undo(string rel)
        {
            var (kind, hash) = entries[rel];
            entries.Remove(rel);
            var full = Full(rel);
            if (!IsOurs(full, hash))
            {
                Info($"{rel} was changed after installing (game update?), left alone");
                return false;
            }
            if (kind == 'A')
            {
                File.Delete(full);
                Info($"removed {rel}");
            }
            else if (File.Exists(BackupOf(rel)))
            {
                File.Copy(BackupOf(rel), full, overwrite: true);
                Info($"restored {rel}");
            }
            else return false;
            return true;
        }

        static string Hash(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }
    }
}
