using System.Reflection;
#if NET
using System.Runtime.Loader;
#endif

namespace PlainAndEasyInjector;

/// Lets the PATCHER (not the game) load the game's DLLs (Assembly-CSharp, UnityEngine.*, Rewired_Core, ...) straight from
/// the game folder. Your hooks mention game types (typeof(DougControl), ...), so .NET has to load those DLLs while patching.
/// This way they don't have to be copied next to the patcher (the .csproj references them with Private="false"), which
/// also keeps the game's files out of anything you publish.
/// Call it at the very start, before any method that uses game types runs.
public static class GameAssemblies
{
    static readonly Dictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> folders = new();
    static bool hooked;

    /// managedDir: the game's DLL folder, e.g. ...\SpookyUnity_Data\Managed
    public static void LoadFrom(string managedDir)
    {
        lock (loaded)
        {
            if (!folders.Contains(managedDir, StringComparer.OrdinalIgnoreCase)) folders.Add(managedDir);
            if (hooked) return;
            hooked = true;
        }
#if NET
        AssemblyLoadContext.Default.Resolving += (context, name) => Load(name, bytes => context.LoadFromStream(new MemoryStream(bytes)));
#else
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => Load(new AssemblyName(e.Name), Assembly.Load);
#endif
    }

    static Assembly? Load(AssemblyName name, Func<byte[], Assembly> load)
    {
        lock (loaded)
        {
            if (name.Name == null) return null;
            if (loaded.TryGetValue(name.Name, out var done)) return done;
            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, name.Name + ".dll");
                if (!File.Exists(path)) continue;
                // loaded from memory, so the file isn't locked and can be replaced while the patcher runs
                var assembly = load(File.ReadAllBytes(path));
                loaded[name.Name] = assembly;
                return assembly;
            }
            return null;
        }
    }
}
