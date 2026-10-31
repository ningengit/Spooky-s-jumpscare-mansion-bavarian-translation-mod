using Mono.Cecil;
using Mono.Cecil.Rocks;
using System.Reflection;

namespace PlainAndEasyInjector;

public sealed partial class Patcher
{
    // ------------------------------------------------------------------
    // Save: writes <dll folder>/patched/<same file names> by default: every changed game DLL, plus the assembly
    // that contains your hooks (rewritten so the patched DLL may call them), this library, and the DLLs those need
    // that the game doesn't have.
    // Returns the path of the patched main DLL, or null if saving failed: the error and the exception are printed
    // to the console, and nothing in the output folder is changed.
    // copyGameFiles: also copy the game's other files from the input folder, so the output folder runs on its own.
    // To put everything into the game (with a backup of the originals), use Install() instead.
    // ------------------------------------------------------------------
    public string? Save(string? outputDir = null, bool copyGameFiles = false)
    {
        try
        {
            outputDir = Path.GetFullPath(outputDir ?? Path.Combine(dllDir, "patched"));
            SaveCore(outputDir, copyGameFiles);
            if (startupEmitted)
                Info($"{RuntimeInitFile} was written to the output folder too: it belongs in {dataRoot}, next to the {Path.GetFileName(dllDir)} folder.");
            return Path.Combine(outputDir, Path.GetFileName(inputPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PlainAndEasyInjector] Saving failed: {ex.Message}");
            Console.Error.WriteLine(ex);
            return null;
        }
    }

    /// Writes everything into outputDir (through a temporary folder, so a failure changes nothing) and returns the file names written.
    List<string> SaveCore(string outputDir, bool copyGameFiles)
    {
        var fileName = Path.GetFileName(inputPath);
        if (SamePath(Path.Combine(outputDir, fileName), inputPath))
            throw new InvalidOperationException($"The output folder is the folder of the original {fileName}, which would be overwritten. " +
                                                "Pick another folder (or use Install(), which keeps a backup of the original).");

        Directory.CreateDirectory(outputDir);
        // Everything is written to a temporary folder first and only moved into place once ALL of it worked,
        // so a failed Save never leaves a half-written or mismatched DLL behind.
        var staging = Path.Combine(outputDir, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var hookModules = new List<(string Path, ModuleDefinition Module, string DestName)>();
        try
        {
            EmitStartup();

            // 1) the assemblies that contain your hooks: read them, make the hooks public
            foreach (var pair in hookMembers)
            {
                var path = pair.Key;
                var destName = Path.GetFileNameWithoutExtension(path) + ".dll";
                if (SamePath(Path.Combine(outputDir, destName), path))
                    throw new InvalidOperationException("Output folder must differ from the folder of your hook assembly.");
                // resolved through the game folder: a single-file program has no netstandard.dll on disk, the game does
                var hookModule = ReadModule(path, resolver);
                hookModules.Add((path, hookModule, destName));
                foreach (var mem in pair.Value)
                    switch (FindMember(hookModule, mem) ?? throw new InvalidOperationException(
                                $"Hook {(mem as Type)?.FullName ?? mem.DeclaringType?.FullName + "." + mem.Name} wasn't found in {path}. Rebuild the project so that copy is up to date."))
                    {
                        case MethodDefinition md: md.IsPublic = true; Publicize(md.DeclaringType); break;
                        case FieldDefinition fd: fd.IsPublic = true; Publicize(fd.DeclaringType); break;
                        case TypeDefinition td: Publicize(td); break;
                    }
            }

            // 2) game members your hooks use directly (possible with a publicized reference, see the .csproj) become public
            foreach (var h in hookModules) PublicizeUsedGameMembers(h.Module);

            // safety net: every reference to a type of your hook assemblies must point to that assembly, or the game
            // fails to load it (TypeLoadException) and your Mod/hooks silently don't run
            CheckHookReferences(hookModules.Select(h => h.Module));

            // 3) the changed game DLLs
            foreach (var m in dirty)
            {
                if ((m.Attributes & ModuleAttributes.StrongNameSigned) != 0)
                {
                    m.Attributes &= ~ModuleAttributes.StrongNameSigned;
                    Warn($"{m.Name} was strong-name signed. Patching breaks that signature, so it's marked as unsigned now.");
                }
                WriteModule(m, Path.Combine(staging, m.Assembly.Name.Name + ".dll"));
            }
            var shipped = new HashSet<string>(dirty.Select(m => m.Assembly.Name.Name), StringComparer.OrdinalIgnoreCase);
            var shippedFiles = new List<string>();

            // 4) the hook assemblies
            foreach (var h in hookModules)
            {
                WriteModule(h.Module, Path.Combine(staging, h.DestName));
                shipped.Add(h.Module.Assembly.Name.Name);
                shippedFiles.Add(h.Path);
            }

            // 5) hooks that use GetMember/SetMember/CallMethod/Argument need this library next to the patched DLL
            var libAssembly = typeof(Patcher).Assembly;
            var libName = libAssembly.GetName().Name!;
            if (!shipped.Contains(libName) && dirty.Any(m => m.AssemblyReferences.Any(r => r.Name == libName)))
            {
                var lib = HookSource(libAssembly);
                CopyWithPdb(lib, Path.Combine(staging, libName + ".dll"));
                shipped.Add(libName);
                shippedFiles.Add(lib);
            }

            // 6) whatever those need that the game doesn't have (e.g. a NuGet package your hooks use)
            CopyDependencies(shippedFiles, shipped, staging);

            // 7) .NET (Core) targets need their runtimeconfig; deliberately NOT the deps.json (it would hide our extra DLLs)
            StageRuntimeConfig(staging);

            // 8) Unity: the list of methods to run at startup (RunAtGameStart / AddMod)
            StageStartupList(staging);

            // 9) optionally the rest of the game
            if (copyGameFiles)
                foreach (var file in Directory.GetFiles(dllDir))
                {
                    var name = Path.GetFileName(file);
                    if (File.Exists(Path.Combine(staging, name)) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                        || SamePath(file, inputPath) || SamePath(file, Path.ChangeExtension(inputPath, ".pdb")))
                        continue;
                    File.Copy(file, Path.Combine(staging, name), overwrite: true);
                }

            return Commit(staging, outputDir);
        }
        finally
        {
            foreach (var h in hookModules) h.Module.Dispose();
            if (Directory.Exists(staging))
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// Copies the DLLs that the shipped assemblies reference, if they sit next to them and the game doesn't have them.
    void CopyDependencies(List<string> shippedFiles, HashSet<string> shipped, string staging)
    {
        var queue = new Queue<string>(shippedFiles);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            List<string> references;
            using (var m = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(file))))
                references = m.AssemblyReferences.Select(r => r.Name).ToList();
            foreach (var name in references)
            {
                if (shipped.Contains(name)) continue;
                if (name.StartsWith("Mono.Cecil", StringComparison.OrdinalIgnoreCase)) continue;   // only needed for patching, not in the game
                if (File.Exists(Path.Combine(dllDir, name + ".dll"))) continue;                    // the game has it already
                var candidate = Path.Combine(Path.GetDirectoryName(file)!, name + ".dll");
                if (!File.Exists(candidate)) continue;                                             // a framework assembly
                shipped.Add(name);
                CopyWithPdb(candidate, Path.Combine(staging, name + ".dll"));
                queue.Enqueue(candidate);
            }
        }
    }

    void StageRuntimeConfig(string staging)
    {
        var name = Path.GetFileNameWithoutExtension(inputPath) + ".runtimeconfig.json";
        var existing = Path.Combine(dllDir, name);
        if (File.Exists(existing))
        {
            File.Copy(existing, Path.Combine(staging, name), overwrite: true);
            return;
        }
        // A .NET (Core) program can't start without one, and the build doesn't always put it next to the DLL
        // (e.g. when the game is a project reference), so write a minimal one.
        const string prefix = ".NETCoreApp,Version=v";
        var framework = TargetFrameworkOf(module);
        if (module.EntryPoint == null || framework == null || !framework.StartsWith(prefix, StringComparison.Ordinal)) return;
        var version = framework.Substring(prefix.Length);   // e.g. "10.0"
        File.WriteAllText(Path.Combine(staging, name),
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net" + version + "\",\n" +
            "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"" + version + ".0\" }\n  }\n}\n");
    }

    /// Moves the staged files into the output folder and returns their names.
    static List<string> Commit(string staging, string outputDir)
    {
        var files = Directory.GetFiles(staging);
        // fail before touching anything if a file we'd replace is in use (e.g. the game is running)
        foreach (var file in files)
        {
            var dest = Path.Combine(outputDir, Path.GetFileName(file));
            if (!File.Exists(dest)) continue;
            try { using (File.Open(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException ex) { throw new IOException($"{dest} is in use (is the game running?).", ex); }
        }
        foreach (var file in files)
        {
            var dest = Path.Combine(outputDir, Path.GetFileName(file));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(file, dest);
        }
        // an old .pdb next to a DLL that was just replaced without one would no longer match it
        var names = new HashSet<string>(files.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
        foreach (var dll in names.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var pdb = Path.ChangeExtension(dll, ".pdb");
            if (!names.Contains(pdb) && File.Exists(Path.Combine(outputDir, pdb))) File.Delete(Path.Combine(outputDir, pdb));
        }
        return names.ToList();
    }

    void CheckHookReferences(IEnumerable<ModuleDefinition> hookModules)
    {
        var owner = new Dictionary<string, string>();   // type full name -> hook assembly name
        foreach (var h in hookModules)
            foreach (var t in h.GetTypes())
                owner[t.FullName] = h.Assembly.Name.Name;
        foreach (var m in dirty)
            foreach (var tr in m.GetTypeReferences())
            {
                var top = tr;
                while (top.DeclaringType != null) top = top.DeclaringType;
                if (owner.TryGetValue(tr.FullName, out var expected) && top.Scope?.Name != expected)
                    throw new InvalidOperationException(
                        $"Internal error: {m.Name} would look for {tr.FullName} in '{top.Scope?.Name}' instead of '{expected}'. Nothing was written. Please report this.");
            }
    }

    // ----- making game members public that hooks use directly -----
    static bool IsEngineOrFramework(string assemblyName)
        => assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal) || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
        || assemblyName.StartsWith("System", StringComparison.Ordinal) || assemblyName.StartsWith("Mono.", StringComparison.Ordinal)
        || assemblyName is "mscorlib" or "netstandard";

    /// With a publicized reference (Krafs.Publicizer, see the .csproj) your hooks can use private game members directly
    /// (instance.useAgg instead of GetMember). Those members are made public in the patched game DLL, so that works at
    /// runtime too. Fields keep Unity's serialization unchanged (they get [NonSerialized] if they weren't serialized before).
    void PublicizeUsedGameMembers(ModuleDefinition hookModule)
    {
        foreach (var tr in hookModule.GetTypeReferences())
            if (GameType(tr) is { } td && !IsVisible(td)) { Publicize(td); Touch(td.Module); }

        foreach (var mr in hookModule.GetMemberReferences())
        {
            var td = GameType(mr.DeclaringType);
            if (td == null) continue;
            switch (mr)
            {
                case FieldReference fr when td.Fields.FirstOrDefault(f => f.Name == fr.Name) is { } fd && !(fd.IsPublic && IsVisible(td)):
                    MakeFieldPublic(fd);
                    Publicize(td);
                    Touch(td.Module);
                    break;
                case MethodReference r when FindGameMethod(td, r) is { } md && !(md.IsPublic && IsVisible(td)):
                    MakeMethodPublic(md);
                    Publicize(td);
                    Touch(td.Module);
                    break;
            }
        }
    }

    TypeDefinition? GameType(TypeReference? tr)
    {
        if (tr == null) return null;
        tr = tr.GetElementType();
        var scope = tr.Scope is ModuleDefinition md ? md.Assembly.Name.Name : tr.Scope?.Name;
        if (scope == null || IsEngineOrFramework(scope) || !resolver.IsGameAssembly(scope)) return null;
        return resolver.TryGame(scope)?.MainModule.GetType(tr.FullName);
    }

    static bool IsVisible(TypeDefinition t)
    {
        for (; t != null; t = t.DeclaringType)
            if (t.IsNested ? !t.IsNestedPublic : !t.IsPublic) return false;
        return true;
    }

    static MethodDefinition? FindGameMethod(TypeDefinition td, MethodReference r)
        => td.Methods.FirstOrDefault(m => m.Name == r.Name && m.Parameters.Count == r.Parameters.Count
                                          && m.GenericParameters.Count == r.GenericParameters.Count
                                          && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(r.Parameters.Select(p => p.ParameterType.FullName)));

    static void MakeFieldPublic(FieldDefinition fd)
    {
        if (fd.IsPublic) return;
        // Unity serializes public fields: a field that wasn't saved before must stay unsaved, or the game's scenes won't load right
        bool wasSerialized = fd.CustomAttributes.Any(a => a.AttributeType.Name == "SerializeField");
        if (!fd.IsStatic && !fd.IsInitOnly && !fd.IsLiteral && !wasSerialized) fd.IsNotSerialized = true;
        fd.IsPublic = true;
    }

    void MakeMethodPublic(MethodDefinition md)
    {
        if (md.IsPublic) return;
        md.IsPublic = true;
        if (!md.IsVirtual) return;
        // an override may not be less visible than the method it overrides, or the type fails to load
        foreach (var m in resolver.OpenedGameModules.ToList())   // a copy: resolving base types may open more DLLs
            foreach (var t in m.GetTypes())
                foreach (var candidate in t.Methods.Where(x => x.IsVirtual && x.Name == md.Name && !x.IsPublic && x != md))
                {
                    MethodDefinition? b = candidate;
                    for (int guard = 0; b != null && guard < 50; guard++)
                    {
                        var next = b.GetBaseMethod();
                        if (next == b) break;
                        if (next == md) { candidate.IsPublic = true; Touch(candidate.Module); break; }
                        b = next;
                    }
                }
    }
}
