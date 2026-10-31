using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PlainAndEasyInjector;

/// Offline patcher. Open a DLL, describe changes in plain C#, then Save() or Install().
/// The target must NOT be running. Cecil handles all the opcode work.
/// Types from other DLLs next to the opened one (e.g. Rewired_Core.dll next to Assembly-CSharp.dll) can be patched
/// too: they're opened automatically, and Save()/Install() write every DLL that was changed.
public sealed partial class Patcher : IDisposable
{
    readonly GameResolver resolver;
    readonly ModuleDefinition module;          // the DLL you opened
    readonly string inputPath;                 // where it lives in the game
    readonly string dllDir;                    // its folder (Unity: ..._Data/Managed)
    readonly string dataRoot;                  // the folder above (Unity: ..._Data): backups and RuntimeInitializeOnLoads.json live here
    readonly InstallState install;
    // DLLs that were changed (the opened one is always written)
    readonly HashSet<ModuleDefinition> dirty = new();
    // hook assembly path -> members that must become public so the patched DLL may call them
    readonly Dictionary<string, List<MemberInfo>> hookMembers = new(StringComparer.OrdinalIgnoreCase);
    // where each method's before-hooks go, so they run in the order you add them
    readonly Dictionary<MethodDefinition, Instruction> beforeAnchors = new();
    // hook assemblies whose target framework was already checked
    readonly HashSet<string> checkedFrameworks = new(StringComparer.OrdinalIgnoreCase);

    Patcher(string path)
    {
        inputPath = Path.GetFullPath(path);
        dllDir = Path.GetDirectoryName(inputPath)!;
        dataRoot = Path.GetDirectoryName(dllDir) ?? dllDir;
        install = InstallState.Load(dataRoot);
        resolver = new GameResolver(dllDir, ReadPathFor);
        try
        {
            var readFrom = ReadPathFor(inputPath);
            if (readFrom != inputPath) Console.WriteLine($"[PlainAndEasyInjector] {Path.GetFileName(inputPath)} is our patched copy; patching the original from the backup instead.");
            module = ReadModule(readFrom, resolver);
            resolver.Register(module.Assembly);
            dirty.Add(module);
            CheckNotPatchedAlready(module, readFrom);
        }
        catch { resolver.Dispose(); throw; }
    }

    public static Patcher Open(string dllPath) => new(dllPath);

    /// Patching an already patched DLL would add every hook twice (and literal edits wouldn't find their text anymore).
    /// Normally Install()'s backup prevents that; this catches the case where the backup or its notes got lost.
    void CheckNotPatchedAlready(ModuleDefinition m, string path)
    {
        var lib = typeof(Patcher).Assembly.GetName().Name;
        if (m.GetType(StartupClassName) != null || m.AssemblyReferences.Any(r => r.Name == lib))
            throw new InvalidOperationException(
                $"{path} was already patched by PlainAndEasyInjector, but there's no backup of the original to start from. " +
                "Get the original back first (Steam: right-click the game > Properties > Installed Files > Verify integrity of game files), then patch again.");
    }

    // ------------------------------------------------------------------
    // Pick a method, then hook it or edit its literals
    //   patcher.Method<Calculator>("Add").AddAfterRun<int>((calc, result) => result * 2);
    // ------------------------------------------------------------------
    public MethodSelector<T> Method<T>(string method) where T : class
        => new(this, TypeFor(typeof(T)), method);

    /// Same, for types you can't use as <T>: static classes (typeof(Rewired.Consts)), structs, ...
    /// The hook then receives the instance as 'object' (null for static methods).
    public MethodSelector<object> Method(Type type, string method)
        => new(this, TypeFor(type), method);

    /// Same, by type name when you can't reference the type ("Namespace.Type", nested: "Outer+Inner").
    public MethodSelector<object> Method(string typeName, string method)
        => new(this, FindType(typeName), method);

    // ------------------------------------------------------------------
    // One-liners (method without overloads, hook without arguments or with Dictionary)
    // ------------------------------------------------------------------
    public void AddBeforeRun<T>(string method, Action<T> hook) where T : class => Method<T>(method).AddBeforeRun(hook);
    public void AddBeforeRun<T>(string method, Action<T, Dictionary<string, Argument>> hook) where T : class => Method<T>(method).AddBeforeRun(hook);

    public void AddAfterRun<T>(string method, Action<T> hook) where T : class => Method<T>(method).AddAfterRun(hook);
    public void AddAfterRun<T>(string method, Action<T, Dictionary<string, Argument>> hook) where T : class => Method<T>(method).AddAfterRun(hook);

    public void AddAfterRun<T, R>(string method, Func<T, R, R> hook) where T : class => Method<T>(method).AddAfterRun(hook);
    public void AddAfterRun<T, R>(string method, Func<T, R, Dictionary<string, Argument>, R> hook) where T : class => Method<T>(method).AddAfterRun(hook);

    // Type versions (static classes, structs, ...): the hook gets the instance as 'object' (null for static methods)
    public void AddBeforeRun(Type type, string method, Action<object> hook) => Method(type, method).AddBeforeRun(hook);
    public void AddBeforeRun(Type type, string method, Action<object, Dictionary<string, Argument>> hook) => Method(type, method).AddBeforeRun(hook);
    public void AddAfterRun(Type type, string method, Action<object> hook) => Method(type, method).AddAfterRun(hook);
    public void AddAfterRun(Type type, string method, Action<object, Dictionary<string, Argument>> hook) => Method(type, method).AddAfterRun(hook);
    public void AddAfterRun<R>(Type type, string method, Func<object, R, R> hook) => Method(type, method).AddAfterRun(hook);
    public void AddAfterRun<R>(Type type, string method, Func<object, R, Dictionary<string, Argument>, R> hook) => Method(type, method).AddAfterRun(hook);

    // ------------------------------------------------------------------
    // Literal edits inside a method.
    // which: Occurrence.All (default), First, Last, or Specific followed by the match numbers, e.g. (..., Occurrence.Specific, 1, 3)
    // ------------------------------------------------------------------
    public int SetStringInMethod<T>(string method, string from, string to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetStringInMethod(from, to, which, positions);
    public int SetBoolInMethod<T>(string method, bool from, bool to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetBoolInMethod(from, to, which, positions);
    public int SetIntInMethod<T>(string method, int from, int to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetIntInMethod(from, to, which, positions);
    public int SetUIntInMethod<T>(string method, uint from, uint to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetUIntInMethod(from, to, which, positions);
    public int SetLongInMethod<T>(string method, long from, long to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetLongInMethod(from, to, which, positions);
    public int SetULongInMethod<T>(string method, ulong from, ulong to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetULongInMethod(from, to, which, positions);
    public int SetShortInMethod<T>(string method, short from, short to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetShortInMethod(from, to, which, positions);
    public int SetUShortInMethod<T>(string method, ushort from, ushort to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetUShortInMethod(from, to, which, positions);
    public int SetByteInMethod<T>(string method, byte from, byte to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetByteInMethod(from, to, which, positions);
    public int SetSByteInMethod<T>(string method, sbyte from, sbyte to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetSByteInMethod(from, to, which, positions);
    public int SetCharInMethod<T>(string method, char from, char to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetCharInMethod(from, to, which, positions);
    public int SetFloatInMethod<T>(string method, float from, float to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetFloatInMethod(from, to, which, positions);
    public int SetDoubleInMethod<T>(string method, double from, double to, Occurrence which = Occurrence.All, params int[] positions) where T : class => Method<T>(method).SetDoubleInMethod(from, to, which, positions);
    public int SetEnumInMethod<T, E>(string method, E from, E to, Occurrence which = Occurrence.All, params int[] positions) where T : class where E : struct, Enum => Method<T>(method).SetEnumInMethod(from, to, which, positions);

    // Type versions
    public int SetStringInMethod(Type type, string method, string from, string to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetStringInMethod(from, to, which, positions);
    public int SetBoolInMethod(Type type, string method, bool from, bool to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetBoolInMethod(from, to, which, positions);
    public int SetIntInMethod(Type type, string method, int from, int to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetIntInMethod(from, to, which, positions);
    public int SetUIntInMethod(Type type, string method, uint from, uint to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetUIntInMethod(from, to, which, positions);
    public int SetLongInMethod(Type type, string method, long from, long to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetLongInMethod(from, to, which, positions);
    public int SetULongInMethod(Type type, string method, ulong from, ulong to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetULongInMethod(from, to, which, positions);
    public int SetShortInMethod(Type type, string method, short from, short to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetShortInMethod(from, to, which, positions);
    public int SetUShortInMethod(Type type, string method, ushort from, ushort to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetUShortInMethod(from, to, which, positions);
    public int SetByteInMethod(Type type, string method, byte from, byte to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetByteInMethod(from, to, which, positions);
    public int SetSByteInMethod(Type type, string method, sbyte from, sbyte to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetSByteInMethod(from, to, which, positions);
    public int SetCharInMethod(Type type, string method, char from, char to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetCharInMethod(from, to, which, positions);
    public int SetFloatInMethod(Type type, string method, float from, float to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetFloatInMethod(from, to, which, positions);
    public int SetDoubleInMethod(Type type, string method, double from, double to, Occurrence which = Occurrence.All, params int[] positions) => Method(type, method).SetDoubleInMethod(from, to, which, positions);
    public int SetEnumInMethod<E>(Type type, string method, E from, E to, Occurrence which = Occurrence.All, params int[] positions) where E : struct, Enum => Method(type, method).SetEnumInMethod(from, to, which, positions);

    public void Dispose()
    {
        resolver.Dispose();   // also disposes every opened game DLL, including the main one
    }

    // ==================================================================
    // Internals
    // ==================================================================

    internal sealed class Hook
    {
        public Hook(MethodReference method, FieldReference? singleton) { Method = method; Singleton = singleton; }
        public MethodReference Method { get; }
        public FieldReference? Singleton { get; }
    }

    internal enum ArgMode { None, ByValue, ByRef, Dictionary }

    /// One method body that literal edits search: the method itself, its state machine, or one of its lambdas.
    internal sealed class CodeBody
    {
        public CodeBody(MethodDefinition method, bool isIterator) { Method = method; IsIterator = isIterator; }
        public MethodDefinition Method { get; }
        /// An iterator's MoveNext: its 'return true/false' means "has more items", not one of your bools.
        public bool IsIterator { get; }
    }

    internal static void Warn(string message) => Console.WriteLine("[PlainAndEasyInjector] Warning: " + message);
    internal static void Info(string message) => Console.WriteLine("[PlainAndEasyInjector] " + message);

    internal void Touch(ModuleDefinition m) => dirty.Add(m);

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static void Publicize(TypeDefinition? t)
    {
        for (; t != null; t = t.DeclaringType)
            if (t.IsNested) t.IsNestedPublic = true; else t.IsPublic = true;
    }

    internal static string CecilName(Type t)
    {
        if (t.IsByRef) return CecilName(t.GetElementType()!) + "&";
        if (t.IsPointer) return CecilName(t.GetElementType()!) + "*";
        if (t.IsArray && t.GetArrayRank() == 1) return CecilName(t.GetElementType()!) + "[]";
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return CecilName(t.GetGenericTypeDefinition()) + "<" + string.Join(",", t.GetGenericArguments().Select(CecilName)) + ">";
        return (t.FullName ?? t.Name).Replace('+', '/');
    }

    /// The game DLL that contains a type (opened on first use).
    ModuleDefinition ModuleFor(Type t)
    {
        var name = t.Assembly.GetName().Name!;
        if (name == module.Assembly.Name.Name) return module;
        return resolver.TryGame(name)?.MainModule
            ?? throw new InvalidOperationException($"{t.FullName} lives in {name}.dll, which isn't in {dllDir}.");
    }

    internal TypeDefinition TypeFor(Type t)
        => ModuleFor(t).GetType(CecilName(t)) ?? throw new InvalidOperationException($"Type '{t.FullName}' not found in {ModuleFor(t).Name}");

    TypeDefinition FindType(string name)
    {
        var cecilName = name.Replace('+', '/');
        return module.GetType(cecilName)
            ?? resolver.OpenedGameModules.Select(m => m.GetType(cecilName)).FirstOrDefault(t => t != null)
            ?? throw new InvalidOperationException($"Type '{name}' not found in {module.Name} (for types from other DLLs use the Type version: Method(typeof(...), ...))");
    }

    // ----- reading / writing modules -----

    /// Reads a module from memory (so the file isn't locked), with its debug symbols if a matching .pdb sits next to it.
    internal static ModuleDefinition ReadModule(string path, IAssemblyResolver? assemblyResolver)
    {
        var bytes = File.ReadAllBytes(path);
        var pdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdb))
        {
            try
            {
                return ModuleDefinition.ReadModule(new MemoryStream(bytes), new ReaderParameters
                {
                    AssemblyResolver = assemblyResolver,
                    ReadSymbols = true,
                    SymbolStream = new MemoryStream(File.ReadAllBytes(pdb)),
                    SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: true),
                });
            }
            catch (Exception ex)
            {
                Warn($"Couldn't read {Path.GetFileName(pdb)} ({ex.Message}); continuing without debug symbols.");
            }
        }
        return ModuleDefinition.ReadModule(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = assemblyResolver });
    }

    /// Writes the module, plus a fresh .pdb if it was read with one (an old .pdb wouldn't match the patched code).
    static void WriteModule(ModuleDefinition m, string file)
    {
        if (m.HasSymbols)
        {
            try
            {
                m.Write(file, new WriterParameters { WriteSymbols = true });
                return;
            }
            catch (Exception ex)
            {
                Warn($"Couldn't write debug symbols for {Path.GetFileName(file)} ({ex.Message}); writing it without them.");
                File.Delete(Path.ChangeExtension(file, ".pdb"));
            }
        }
        m.Write(file);
    }

    static void CopyWithPdb(string source, string dest)
    {
        File.Copy(source, dest, overwrite: true);
        var pdb = Path.ChangeExtension(source, ".pdb");
        if (File.Exists(pdb)) File.Copy(pdb, Path.ChangeExtension(dest, ".pdb"), overwrite: true);
    }

    static string? TargetFrameworkOf(ModuleDefinition m)
        => m.Assembly?.CustomAttributes
               .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")?
               .ConstructorArguments.FirstOrDefault().Value as string;

    // ----- hook assemblies -----

    /// The file that gets shipped next to the patched DLL for an assembly containing hooks (or this library).
    /// Prefers the in-game build in the "ingame" folder (see PlainAndEasyInjector.csproj) over the running assembly,
    /// because the game has to be able to load it: Unity can't load a net10.0 assembly.
    string HookSource(Assembly a)
    {
        var running = a.Location;
        var name = a.GetName().Name + ".dll";
        string chosen;
        if (string.IsNullOrEmpty(running))
        {
            // single-file publish: the assembly has no file of its own, so the in-game copy must sit next to the program
            chosen = Path.Combine(AppContext.BaseDirectory, "ingame", name);
            if (!File.Exists(chosen))
                throw new InvalidOperationException($"{a.GetName().Name} has no file on disk (single-file publish?) and {chosen} doesn't exist.");
        }
        else
        {
            var inGame = Path.Combine(Path.GetDirectoryName(running)!, "ingame", Path.GetFileName(running));
            chosen = File.Exists(inGame) ? inGame : running;
            if (chosen == inGame && checkedFrameworks.Contains(chosen) == false
                && File.GetLastWriteTimeUtc(inGame) < File.GetLastWriteTimeUtc(running).AddMinutes(-1))
                Warn($"{inGame} is older than {Path.GetFileName(running)}. Rebuild so the in-game copy has your latest hooks.");
        }
        if (checkedFrameworks.Add(chosen)) CheckFramework(chosen);
        return chosen;
    }

    void CheckFramework(string path)
    {
        string? framework;
        using (var m = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(path))))
            framework = TargetFrameworkOf(m);
        var corlib = module.TypeSystem.CoreLibrary.Name;
        if (framework != null && framework.StartsWith(".NETCoreApp", StringComparison.Ordinal) && corlib is "mscorlib" or "netstandard")
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is built for {framework}, but {module.Name} is a Unity / .NET Framework / .NET Standard assembly, " +
                "which can't load it. Hooks must be built for netstandard2.1 (or the .NET Framework version the game uses): " +
                "see the 'ingame' build in PlainAndEasyInjector.csproj.");
    }

    void Track(MemberInfo mi)
    {
        var assembly = mi is Type t ? t.Assembly : mi.DeclaringType!.Assembly;
        var path = HookSource(assembly);
        if (!hookMembers.TryGetValue(path, out var list)) hookMembers[path] = list = new();
        list.Add(mi);
    }

    /// Finds a tracked hook member in the assembly that gets shipped (by name, since that may be a different build).
    static IMemberDefinition? FindMember(ModuleDefinition hookModule, MemberInfo mi)
    {
        if (mi is Type t) return hookModule.GetType(CecilName(t));
        var type = hookModule.GetType(CecilName(mi.DeclaringType!));
        if (type == null) return null;
        if (mi is FieldInfo f) return type.Fields.FirstOrDefault(x => x.Name == f.Name);
        var wanted = ((MethodBase)mi).GetParameters().Select(p => CecilName(p.ParameterType)).ToList();
        return type.Methods.FirstOrDefault(x => x.Name == mi.Name && x.Parameters.Select(p => Strip(p.ParameterType).FullName).SequenceEqual(wanted));
    }

    // ----- type mapping -----

    /// .NET's own assemblies (their types go through 'netstandard' in the game). Decided by NAME, never by folder: in a
    /// single-file or self-extracting program, .NET and your own DLLs can sit in the same folder (or none at all), and
    /// treating your Mod as a .NET type made the game look for it in netstandard (TypeLoadException).
    static bool IsFramework(Assembly a)
    {
        if (a == typeof(object).Assembly) return true;
        var name = a.GetName().Name ?? "";
        return name is "mscorlib" or "netstandard" or "System" || name.StartsWith("System.", StringComparison.Ordinal);
    }

    /// Maps a runtime System.Type to a reference valid INSIDE the target module.
    /// Framework types go through 'netstandard' (or the target's own core library if that IS netstandard): every
    /// modern runtime resolves it, so Unity / .NET Framework / .NET targets work no matter what the patcher runs on.
    internal TypeReference Map(Type t, ModuleDefinition target)
    {
        var ts = target.TypeSystem;
        if (t.IsByRef) return new ByReferenceType(Map(t.GetElementType()!, target));
        if (t.IsPointer) return new PointerType(Map(t.GetElementType()!, target));
        if (t.IsArray)
            return t.GetArrayRank() == 1 && t == t.GetElementType()!.MakeArrayType()
                ? new ArrayType(Map(t.GetElementType()!, target))
                : new ArrayType(Map(t.GetElementType()!, target), t.GetArrayRank());
        if (t.IsGenericParameter) throw new NotSupportedException("Generic hook methods aren't supported.");
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var inst = new GenericInstanceType(Map(t.GetGenericTypeDefinition(), target));
            foreach (var a in t.GetGenericArguments()) inst.GenericArguments.Add(Map(a, target));
            return inst;
        }
        TypeReference? prim = t.FullName switch
        {
            "System.Void" => ts.Void,
            "System.Boolean" => ts.Boolean,
            "System.Char" => ts.Char,
            "System.SByte" => ts.SByte,
            "System.Byte" => ts.Byte,
            "System.Int16" => ts.Int16,
            "System.UInt16" => ts.UInt16,
            "System.Int32" => ts.Int32,
            "System.UInt32" => ts.UInt32,
            "System.Int64" => ts.Int64,
            "System.UInt64" => ts.UInt64,
            "System.Single" => ts.Single,
            "System.Double" => ts.Double,
            "System.String" => ts.String,
            "System.Object" => ts.Object,
            "System.IntPtr" => ts.IntPtr,
            "System.UIntPtr" => ts.UIntPtr,
            _ => null
        };
        if (prim != null) return prim;
        if (t.Assembly.GetName().Name == target.Assembly.Name.Name)
            return target.GetType(CecilName(t)) ?? throw new InvalidOperationException($"Type '{t.FullName}' not found in {target.Name}");
        if (IsFramework(t.Assembly)) return TypeRefIn(t, FrameworkScope(target), target);
        // reuse the game's own reference to that assembly if it has one (keeps the game's version), else add one
        var asmName = t.Assembly.GetName().Name;
        var existing = target.AssemblyReferences.FirstOrDefault(r => r.Name == asmName);
        return existing != null ? TypeRefIn(t, existing, target) : target.ImportReference(t);
    }

    static TypeReference TypeRefIn(Type t, IMetadataScope scope, ModuleDefinition target)
    {
        var r = t.IsNested
            ? new TypeReference("", t.Name, target, scope, t.IsValueType) { DeclaringType = TypeRefIn(t.DeclaringType!, scope, target) }
            : new TypeReference(t.Namespace ?? "", t.Name, target, scope, t.IsValueType);
        // an open generic type needs its generic parameters, or Cecil can't write signatures that use it
        if (t.IsGenericTypeDefinition)
            foreach (var gp in t.GetGenericArguments()) r.GenericParameters.Add(new GenericParameter(gp.Name, r));
        return r;
    }

    static IMetadataScope FrameworkScope(ModuleDefinition target)
    {
        var corlib = target.TypeSystem.CoreLibrary;
        if (corlib.Name == "netstandard") return corlib;
        var existing = target.AssemblyReferences.FirstOrDefault(r => r.Name == "netstandard");
        if (existing != null) return existing;
        var reference = new AssemblyNameReference("netstandard", new Version(2, 0, 0, 0))
        {
            PublicKeyToken = new byte[] { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 }
        };
        target.AssemblyReferences.Add(reference);
        return reference;
    }

    internal MethodReference MethodRef(MethodBase mb, ModuleDefinition target)
    {
        var returnType = mb is MethodInfo mi ? mi.ReturnType : typeof(void);
        var mr = new MethodReference(mb.Name, Map(returnType, target), Map(mb.DeclaringType!, target)) { HasThis = !mb.IsStatic };
        foreach (var p in mb.GetParameters()) mr.Parameters.Add(new ParameterDefinition(Map(p.ParameterType, target)));
        return mr;
    }

    // ----- parameters -----

    /// The type without modifiers like the 'modreq(InAttribute)' that 'in' parameters can carry.
    internal static TypeReference Strip(TypeReference t)
    {
        while (t is IModifierType or PinnedType) t = ((TypeSpecification)t).ElementType;
        return t is ByReferenceType br ? new ByReferenceType(Strip(br.ElementType)) : t;
    }

    internal static IEnumerable<string> ParamNames(MethodDefinition m, bool loose)
        => m.Parameters.Select(p => { var n = Strip(p.ParameterType).FullName; return loose ? Loose(n) : n; });

    /// "System.Int32&" -> "System.Int32": a hook declares ref/out/in parameters by their plain type.
    internal static string Loose(string typeName) => typeName.EndsWith("&", StringComparison.Ordinal) ? typeName.Substring(0, typeName.Length - 1) : typeName;

    internal sealed class ParamShape
    {
        public ParamShape(ParameterDefinition param) => Param = param;
        public ParameterDefinition Param { get; }
        public bool IsByRef { get; set; }      // ref / out / in
        public bool IsOut { get; set; }
        public bool IsReadOnly { get; set; }   // 'in': may be read, never written
        public TypeReference Element { get; set; } = null!;   // the type behind the reference (or the type itself)
        public bool IsPointer { get; set; }    // int*, function pointers: handed to hooks as IntPtr
        public bool IsByRefLike { get; set; }  // Span<T> & co.: can't be boxed into an Argument
    }

    internal static ParamShape Shape(ParameterDefinition p)
    {
        var raw = p.ParameterType;
        var t = Strip(raw);
        var s = new ParamShape(p);
        if (t is ByReferenceType br) { s.IsByRef = true; t = br.ElementType; }
        s.Element = t;
        s.IsOut = s.IsByRef && p.IsOut && !p.IsIn;
        s.IsReadOnly = s.IsByRef && (HasInModifier(raw) || p.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute"));
        s.IsPointer = t is PointerType or FunctionPointerType;
        s.IsByRefLike = !s.IsPointer && IsByRefLike(t);
        return s;
    }

    static bool HasInModifier(TypeReference t)
    {
        for (; t is TypeSpecification spec; t = spec.ElementType)
            if (t is RequiredModifierType { ModifierType.FullName: "System.Runtime.InteropServices.InAttribute" }) return true;
        return false;
    }

    static bool IsByRefLike(TypeReference t)
    {
        if (t is GenericParameter || !t.IsValueType) return false;
        if (t.FullName is "System.TypedReference" or "System.ArgIterator" or "System.RuntimeArgumentHandle") return true;
        try { return t.Resolve()?.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute") == true; }
        catch { return false; }   // can't resolve it: assume a normal struct
    }

    internal static VariableDefinition AddLocal(MethodDefinition m, TypeReference type)
    {
        var v = new VariableDefinition(type);
        m.Body.Variables.Add(v);
        m.Body.InitLocals = true;
        return v;
    }

    // ----- state machines (iterators / async) -----

    internal const string IteratorKind = "IteratorStateMachineAttribute";
    internal const string AsyncKind = "AsyncStateMachineAttribute";
    internal const string AsyncIteratorKind = "AsyncIteratorStateMachineAttribute";

    /// Iterator/async methods keep their real code in a hidden state-machine class (MoveNext).
    internal static (string Kind, MethodDefinition MoveNext)? StateMachineOf(MethodDefinition m)
    {
        foreach (var a in m.CustomAttributes)
            if (a.AttributeType.Name is IteratorKind or AsyncKind or AsyncIteratorKind
                && a.ConstructorArguments.Count > 0 && a.ConstructorArguments[0].Value is TypeReference tr
                && SafeResolve(tr)?.Methods.FirstOrDefault(x => x.Name == "MoveNext") is { } moveNext)
                return (a.AttributeType.Name, moveNext);
        return null;
    }

    static TypeDefinition? SafeResolve(TypeReference r) { try { return r.Resolve(); } catch { return null; } }
    static MethodDefinition? SafeResolve(MethodReference r) { try { return r.Resolve(); } catch { return null; } }

    /// What literal edits search: the method, its iterator/async body (the compiler moves that code into a hidden
    /// MoveNext), and its lambdas and local functions (hidden methods named "<Method>b__..." / "<Method>g__..."),
    /// in that order.
    internal static List<CodeBody> BodiesOf(MethodDefinition root)
    {
        var result = new List<CodeBody>();
        var seen = new HashSet<MethodDefinition>();
        var prefix = "<" + root.Name + ">";
        void Visit(MethodDefinition m, bool isIterator)
        {
            if (!m.HasBody || !seen.Add(m)) return;
            result.Add(new CodeBody(m, isIterator));
            if (StateMachineOf(m) is { } sm) Visit(sm.MoveNext, sm.Kind == IteratorKind);
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is MethodReference mr && mr.Name.StartsWith(prefix, StringComparison.Ordinal)
                    && SafeResolve(mr) is { } d && d.Module == root.Module)
                    Visit(d, false);
        }
        Visit(root, false);
        return result;
    }

    // ----- hooks -----

    /// Accepts static methods (any visibility) AND lambdas that don't capture variables.
    internal Hook PrepareHook(Delegate d, ModuleDefinition target)
    {
        var mi = d.Method;
        if (mi.IsGenericMethod || mi.DeclaringType!.IsGenericType)
            throw new NotSupportedException("Hooks can't be generic methods.");

        FieldReference? singleton = null;
        if (!mi.IsStatic)
        {
            // A non-capturing lambda lives on the compiler's hidden "<>c" class, reachable via its static "<>9" instance.
            var instance = d.Target;
            var tt = instance?.GetType();
            var field = tt?.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            bool isPlainLambda = tt != null
                && tt.GetCustomAttribute<CompilerGeneratedAttribute>() != null
                && tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0
                && field != null && ReferenceEquals(field.GetValue(null), instance);
            if (!isPlainLambda)
                throw new InvalidOperationException(
                    "This hook captures variables or 'this'. The patched DLL runs without your patcher, so there is nothing to capture. " +
                    "Use constants, static fields, or a static method instead.");
            singleton = new FieldReference(field!.Name, Map(field.FieldType, target), Map(field.DeclaringType!, target));
            Track(field);
        }
        Track(mi);
        return new Hook(MethodRef(mi, target), singleton);
    }

    /// Validates, then injects a hook into a method.
    internal void Inject(MethodDefinition m, Delegate hookDelegate, bool before, bool returnsValue, ArgMode mode)
    {
        if (!m.IsStatic && m.DeclaringType.IsValueType)
            throw new NotSupportedException(
                $"{m.DeclaringType.Name}.{m.Name} belongs to a struct. Hooks receive the instance as an object, but inside a struct " +
                "method 'this' is a pointer, not an object, so this isn't supported. (Literal edits like SetIntInMethod work fine on structs.)");

        // After-hooks without a result on iterators/async methods run when the work FINISHES (inside the state machine).
        // Hooks that take the result still run in the method itself and get the IEnumerator/Task, which they may wrap.
        var sm = before || returnsValue ? null : StateMachineOf(m);
        if (sm != null)
        {
            if (sm.Value.Kind == AsyncIteratorKind)
                throw new NotSupportedException($"{m.Name} is an async iterator (IAsyncEnumerable); after-hooks on those aren't supported.");
            if (mode != ArgMode.None)
                throw new NotSupportedException(
                    $"{m.Name} is an iterator/async method. Its after-hooks run when it finishes and can't read the arguments: " +
                    "leave them out, or read them in an AddBeforeRun hook.");
            if (sm.Value.MoveNext.DeclaringType.HasGenericParameters)
                throw new NotSupportedException($"{m.Name} is a generic (or in a generic class) iterator/async method; after-hooks on those aren't supported.");
        }

        var hook = PrepareHook(hookDelegate, m.Module);
        Touch(m.Module);
        if (before) InjectBefore(m, hook, mode);
        else if (sm == null) InjectAfter(m, hook, returnsValue, mode);
        else if (sm.Value.Kind == IteratorKind) InjectAfterIterator(m, sm.Value.MoveNext, hook);
        else InjectAfterAsync(m, sm.Value.MoveNext, hook);
    }

    // stack layout for the hook call: [lambda instance] self [result] [all method args or dictionary]
    List<Instruction> CallHook(ILProcessor il, MethodDefinition m, Hook h, VariableDefinition? result, ArgMode mode, bool isBefore)
    {
        var code = new List<Instruction>();
        var shapes = m.Parameters.Select(Shape).ToList();
        VariableDefinition? array = null, dict = null;
        if (mode == ArgMode.Dictionary) (array, dict) = EmitBuildArguments(il, m, shapes, isBefore, code);

        if (h.Singleton != null) code.Add(il.Create(OpCodes.Ldsfld, h.Singleton));
        code.Add(m.IsStatic ? il.Create(OpCodes.Ldnull) : il.Create(OpCodes.Ldarg_0));
        if (result != null) code.Add(il.Create(OpCodes.Ldloc, result));

        switch (mode)
        {
            case ArgMode.Dictionary:
                code.Add(il.Create(OpCodes.Ldloc, dict!));
                break;
            case ArgMode.ByValue:
                foreach (var s in shapes)
                {
                    code.Add(il.Create(OpCodes.Ldarg, s.Param));
                    if (s.IsByRef) code.Add(il.Create(OpCodes.Ldobj, s.Element));   // the value behind ref/out/in
                }
                break;
            case ArgMode.ByRef:
                // ByRef passes the ADDRESS of each argument, so the hook can change what the method sees
                foreach (var s in shapes)
                {
                    if (!s.IsByRef) code.Add(il.Create(OpCodes.Ldarga, s.Param));
                    else if (!s.IsReadOnly) code.Add(il.Create(OpCodes.Ldarg, s.Param));   // ref/out: already an address
                    else
                    {
                        // 'in' arguments must not change, so the hook gets a copy
                        var copy = AddLocal(m, s.Element);
                        code.Add(il.Create(OpCodes.Ldarg, s.Param));
                        code.Add(il.Create(OpCodes.Ldobj, s.Element));
                        code.Add(il.Create(OpCodes.Stloc, copy));
                        code.Add(il.Create(OpCodes.Ldloca, copy));
                    }
                }
                break;
        }

        code.Add(il.Create(OpCodes.Call, h.Method));
        if (mode == ArgMode.Dictionary) EmitWriteBack(il, m, shapes, array!, isBefore, code);
        return code;
    }

    /// Builds Argument[] { new Argument(name, (object)value, typeof(T), readOnly), ... } and the dictionary the hook gets.
    (VariableDefinition array, VariableDefinition dict) EmitBuildArguments(ILProcessor il, MethodDefinition m, List<ParamShape> shapes, bool isBefore, List<Instruction> code)
    {
        var target = m.Module;
        var argType = Map(typeof(Argument), target);
        var argCtor = MethodRef(typeof(Argument).GetConstructor(new[] { typeof(string), typeof(object), typeof(Type), typeof(bool) })!, target);
        var typeFromHandle = MethodRef(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!, target);
        var toDictionary = MethodRef(typeof(HookRuntime).GetMethod(nameof(HookRuntime.ToDictionary))!, target);
        var intPtr = target.TypeSystem.IntPtr;
        var array = AddLocal(m, new ArrayType(argType));
        var dict = AddLocal(m, Map(typeof(Dictionary<string, Argument>), target));

        code.Add(il.Create(OpCodes.Ldc_I4, shapes.Count));
        code.Add(il.Create(OpCodes.Newarr, argType));
        code.Add(il.Create(OpCodes.Stloc, array));
        for (int i = 0; i < shapes.Count; i++)
        {
            var s = shapes[i];
            code.Add(il.Create(OpCodes.Ldloc, array));
            code.Add(il.Create(OpCodes.Ldc_I4, i));
            code.Add(il.Create(OpCodes.Ldstr, string.IsNullOrEmpty(s.Param.Name) ? "arg" + i : s.Param.Name));

            // the value, as an object
            if (s.IsByRefLike || (isBefore && s.IsOut))
                code.Add(il.Create(OpCodes.Ldnull));   // Span<T> & co. can't be boxed; an 'out' parameter has no value before the method runs
            else
            {
                code.Add(il.Create(OpCodes.Ldarg, s.Param));
                if (s.IsPointer)
                {
                    if (s.IsByRef) code.Add(il.Create(OpCodes.Ldind_I));
                    code.Add(il.Create(OpCodes.Box, intPtr));
                }
                else
                {
                    if (s.IsByRef) code.Add(il.Create(OpCodes.Ldobj, s.Element));
                    if (s.Element.IsValueType || s.Element.IsGenericParameter) code.Add(il.Create(OpCodes.Box, s.Element));
                }
            }

            // its declared type, so SetArgValue can convert to it and args.Get<T>() can find it
            code.Add(il.Create(OpCodes.Ldtoken, s.IsPointer ? intPtr : s.Element));
            code.Add(il.Create(OpCodes.Call, typeFromHandle));
            code.Add(il.Create(s.IsReadOnly || s.IsByRefLike ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            code.Add(il.Create(OpCodes.Newobj, argCtor));
            code.Add(il.Create(OpCodes.Stelem_Ref));
        }
        code.Add(il.Create(OpCodes.Ldloc, array));
        code.Add(il.Create(OpCodes.Call, toDictionary));
        code.Add(il.Create(OpCodes.Stloc, dict));
        return (array, dict);
    }

    /// Copies values changed with SetArgValue back into the method's arguments.
    void EmitWriteBack(ILProcessor il, MethodDefinition m, List<ParamShape> shapes, VariableDefinition array, bool isBefore, List<Instruction> code)
    {
        var target = m.Module;
        var argIsModified = MethodRef(typeof(Argument).GetProperty(nameof(Argument.IsModified))!.GetGetMethod()!, target);
        var argGetValue = MethodRef(typeof(Argument).GetProperty(nameof(Argument.Value))!.GetGetMethod()!, target);
        var intPtr = target.TypeSystem.IntPtr;
        VariableDefinition? argVar = null;

        for (int i = 0; i < shapes.Count; i++)
        {
            var s = shapes[i];
            // after the method ran only ref/out arguments still matter; 'in' and Span-like ones can't be changed
            if (s.IsReadOnly || s.IsByRefLike || (!isBefore && !s.IsByRef)) continue;
            argVar ??= AddLocal(m, Map(typeof(Argument), target));
            var skip = il.Create(OpCodes.Nop);

            // var arg = arguments[i]; if (arg.IsModified) { ... }
            code.Add(il.Create(OpCodes.Ldloc, array));
            code.Add(il.Create(OpCodes.Ldc_I4, i));
            code.Add(il.Create(OpCodes.Ldelem_Ref));
            code.Add(il.Create(OpCodes.Stloc, argVar));
            code.Add(il.Create(OpCodes.Ldloc, argVar));
            code.Add(il.Create(OpCodes.Callvirt, argIsModified));
            code.Add(il.Create(OpCodes.Brfalse, skip));

            if (s.IsByRef) code.Add(il.Create(OpCodes.Ldarg, s.Param));   // the address to store to
            code.Add(il.Create(OpCodes.Ldloc, argVar));
            code.Add(il.Create(OpCodes.Callvirt, argGetValue));
            var t = s.IsPointer ? intPtr : s.Element;
            code.Add(il.Create(t.IsValueType || t.IsGenericParameter ? OpCodes.Unbox_Any : OpCodes.Castclass, t));
            if (!s.IsByRef) code.Add(il.Create(OpCodes.Starg, s.Param));
            else code.Add(s.IsPointer ? il.Create(OpCodes.Stind_I) : il.Create(OpCodes.Stobj, s.Element));
            code.Add(skip);
        }
    }

    internal void InjectBefore(MethodDefinition m, Hook hook, ArgMode mode)
    {
        m.Body.SimplifyMacros();
        var il = m.Body.GetILProcessor();
        var anchor = BeforeAnchor(m);
        foreach (var ins in CallHook(il, m, hook, null, mode, isBefore: true))
            il.InsertBefore(anchor, ins);
        m.Body.OptimizeMacros();
    }

    /// Where before-hooks go: the start of the method, or for constructors right after the call to the base (or
    /// another) constructor, because before that 'this' isn't a usable object yet. It's fixed per method, so hooks
    /// added later go after earlier ones and run in the order you add them.
    Instruction BeforeAnchor(MethodDefinition m)
    {
        if (beforeAnchors.TryGetValue(m, out var anchor)) return anchor;
        anchor = BaseConstructorCall(m)?.Next ?? m.Body.Instructions[0];
        return beforeAnchors[m] = anchor;
    }

    /// The ': base(...)' / ': this(...)' call of an instance constructor, or null.
    internal static Instruction? BaseConstructorCall(MethodDefinition m)
    {
        if (!m.IsConstructor || m.IsStatic || m.DeclaringType.IsValueType) return null;
        return m.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference { Name: ".ctor" } r
            && (SameType(r.DeclaringType, m.DeclaringType) || (m.DeclaringType.BaseType is { } bt && SameType(r.DeclaringType, bt))));
    }

    internal static bool SameType(TypeReference a, TypeReference b) => a.GetElementType().FullName == b.GetElementType().FullName;

    internal void InjectAfter(MethodDefinition m, Hook hook, bool returnsValue, ArgMode mode)
    {
        bool isVoid = m.ReturnType.MetadataType == MetadataType.Void;
        if (returnsValue && isVoid) throw new InvalidOperationException($"{m.Name} returns void; use the AddAfterRun overload without a return value.");
        if (returnsValue && Strip(m.ReturnType) is ByReferenceType)
            throw new NotSupportedException($"{m.Name} returns a reference ('ref' return); hooks that change the result don't support that.");
        // !returnsValue on a non-void method is fine: the original result is passed through untouched.
        if (returnsValue && hook.Method.ReturnType.FullName != m.ReturnType.FullName)
            throw new InvalidOperationException($"{m.Name} returns {m.ReturnType.FullName} but your hook returns {hook.Method.ReturnType.FullName}.");

        m.Body.SimplifyMacros();
        if (!m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ret))
        {
            Warn($"{m.DeclaringType.Name}.{m.Name} never returns normally (it always throws), so its after-hook would never run.");
            m.Body.OptimizeMacros();
            return;
        }
        var il = m.Body.GetILProcessor();
        var value = isVoid ? null : AddLocal(m, m.ReturnType);
        var epilogue = CallHook(il, m, hook, returnsValue ? value : null, mode, isBefore: false);
        if (returnsValue) epilogue.Add(il.Create(OpCodes.Stloc, value!));
        if (value != null) epilogue.Add(il.Create(OpCodes.Ldloc, value));
        epilogue.Add(il.Create(OpCodes.Ret));
        RedirectReturns(m, il, epilogue, value);
        m.Body.OptimizeMacros();
    }

    /// Replaces every 'ret' with a jump to ONE shared epilogue appended at the end, so the hook call exists once
    /// per method no matter how many 'return's it has. 'value' receives the return value (null for void).
    static void RedirectReturns(MethodDefinition m, ILProcessor il, List<Instruction> epilogue, VariableDefinition? value)
    {
        var instructions = m.Body.Instructions;
        var rets = instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        // a 'tail.' call must be followed by 'ret', which is about to become a jump
        foreach (var ins in instructions.Where(i => i.OpCode == OpCodes.Tail).ToList()) ins.OpCode = OpCodes.Nop;
        var start = epilogue[0];
        // try/catch blocks that run to the very end of the method must not swallow the epilogue
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            eh.TryEnd ??= start;
            eh.HandlerEnd ??= start;
        }
        foreach (var ins in epilogue) il.Append(ins);
        foreach (var ret in rets)
        {
            // The original 'ret' object is reused, so branches that targeted it still land on the right spot.
            if (value != null)
            {
                ret.OpCode = OpCodes.Stloc;
                ret.Operand = value;
                il.InsertAfter(ret, il.Create(OpCodes.Br, start));
            }
            else
            {
                ret.OpCode = OpCodes.Br;
                ret.Operand = start;
            }
        }
    }

    /// Replaces a 'ret' with 'code' followed by a new 'ret' (reusing the 'ret' object keeps branches to it working).
    internal static void InsertAtReturn(ILProcessor il, Instruction ret, params Instruction[] code)
    {
        ret.OpCode = code[0].OpCode;
        ret.Operand = code[0].Operand;
        var cur = ret;
        foreach (var ins in code.Skip(1).Append(il.Create(OpCodes.Ret))) { il.InsertAfter(cur, ins); cur = ins; }
    }

    static List<Instruction> CallStateMachineHook(ILProcessor il, Hook h, FieldReference? self)
    {
        var code = new List<Instruction>();
        if (h.Singleton != null) code.Add(il.Create(OpCodes.Ldsfld, h.Singleton));
        if (self == null) code.Add(il.Create(OpCodes.Ldnull));
        else
        {
            code.Add(il.Create(OpCodes.Ldarg_0));   // the state machine
            code.Add(il.Create(OpCodes.Ldfld, self));
        }
        code.Add(il.Create(OpCodes.Call, h.Method));
        return code;
    }

    /// Iterators (and Unity coroutines): the hook runs when MoveNext returns false, i.e. the iteration reached its end.
    void InjectAfterIterator(MethodDefinition stub, MethodDefinition moveNext, Hook hook)
    {
        var self = ThisFieldOf(stub, moveNext.DeclaringType, isAsync: false);
        moveNext.Body.SimplifyMacros();
        var il = moveNext.Body.GetILProcessor();
        var hasMore = AddLocal(moveNext, moveNext.Module.TypeSystem.Boolean);
        var done = il.Create(OpCodes.Ldloc, hasMore);
        var epilogue = new List<Instruction> { il.Create(OpCodes.Ldloc, hasMore), il.Create(OpCodes.Brtrue, done) };
        epilogue.AddRange(CallStateMachineHook(il, hook, self));
        epilogue.Add(done);
        epilogue.Add(il.Create(OpCodes.Ret));
        RedirectReturns(moveNext, il, epilogue, hasMore);
        moveNext.Body.OptimizeMacros();
    }

    /// Async methods: the hook runs right before the result is handed to the Task (only if no exception was thrown).
    void InjectAfterAsync(MethodDefinition stub, MethodDefinition moveNext, Hook hook)
    {
        var completions = moveNext.Body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference { Name: "SetResult" } r && r.DeclaringType.Name.Contains("MethodBuilder"))
            .ToList();
        if (completions.Count == 0) throw new NotSupportedException($"Couldn't find where the async method {stub.Name} completes.");
        var self = ThisFieldOf(stub, moveNext.DeclaringType, isAsync: true);
        moveNext.Body.SimplifyMacros();
        var il = moveNext.Body.GetILProcessor();
        foreach (var completion in completions)
            foreach (var ins in CallStateMachineHook(il, hook, self))
                il.InsertBefore(completion, ins);
        moveNext.Body.OptimizeMacros();
    }

    /// The state machine's copy of 'this' (null for static methods). The compiler only keeps one if the method
    /// itself uses 'this', so it's added when missing.
    static FieldReference? ThisFieldOf(MethodDefinition stub, TypeDefinition sm, bool isAsync)
    {
        if (stub.IsStatic) return null;
        var existing = sm.Fields.FirstOrDefault(f => f.Name == "<>4__this");
        if (existing != null) return existing;

        var field = new FieldDefinition("<>4__this", Mono.Cecil.FieldAttributes.Public, stub.DeclaringType);
        sm.Fields.Add(field);
        stub.Body.SimplifyMacros();
        var il = stub.Body.GetILProcessor();
        if (isAsync)
        {
            // set it right before builder.Start(ref stateMachine), which runs the first part of the method
            var start = stub.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call
                && i.Operand is MethodReference { Name: "Start" } && i.Previous?.Operand is VariableDefinition)
                ?? throw new NotSupportedException($"Couldn't find where the async method {stub.Name} starts.");
            var smVar = (VariableDefinition)start.Previous.Operand;
            il.InsertBefore(start, il.Create(smVar.VariableType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, smVar));
            il.InsertBefore(start, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(start, il.Create(OpCodes.Stfld, field));
        }
        else
        {
            // the method returns the new state machine: set the field on it first
            foreach (var ret in stub.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
                InsertAtReturn(il, ret, il.Create(OpCodes.Dup), il.Create(OpCodes.Castclass, sm), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Stfld, field));

            // an IEnumerable iterator may hand out a fresh copy of itself in GetEnumerator(): copy the field over
            foreach (var getEnumerator in sm.Methods.Where(x => x.HasBody && x.Name.EndsWith("GetEnumerator", StringComparison.Ordinal)))
            {
                var body = getEnumerator.Body;
                if (!body.Instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r && SameType(r.DeclaringType, sm))) continue;
                body.SimplifyMacros();
                var gil = body.GetILProcessor();
                foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
                    InsertAtReturn(gil, ret, gil.Create(OpCodes.Dup), gil.Create(OpCodes.Castclass, sm), gil.Create(OpCodes.Ldarg_0),
                                   gil.Create(OpCodes.Ldfld, field), gil.Create(OpCodes.Stfld, field));
                body.OptimizeMacros();
            }
        }
        stub.Body.OptimizeMacros();
        return field;
    }
}

/// Resolves the game's DLLs from the game folder, reading each one only once (from memory, so nothing gets locked).
/// Every DLL of the game that gets opened is a possible patch target.
sealed class GameResolver : DefaultAssemblyResolver
{
    readonly string dir;
    readonly Func<string, string> readPathFor;
    readonly Dictionary<string, AssemblyDefinition> game = new(StringComparer.OrdinalIgnoreCase);

    public GameResolver(string dir, Func<string, string> readPathFor)
    {
        this.dir = dir;
        this.readPathFor = readPathFor;
        AddSearchDirectory(dir);
    }

    public IEnumerable<ModuleDefinition> OpenedGameModules => game.Values.Select(a => a.MainModule);

    public void Register(AssemblyDefinition a) => game[a.Name.Name] = a;

    public bool IsGameAssembly(string name) => game.ContainsKey(name) || File.Exists(Path.Combine(dir, name + ".dll"));

    public AssemblyDefinition? TryGame(string name)
    {
        if (game.TryGetValue(name, out var a)) return a;
        var path = Path.Combine(dir, name + ".dll");
        if (!File.Exists(path)) return null;
        a = Patcher.ReadModule(readPathFor(path), this).Assembly;
        game[name] = a;
        return a;
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name) => TryGame(name.Name) ?? base.Resolve(name);

    protected override void Dispose(bool disposing)
    {
        foreach (var a in game.Values) a.Dispose();
        game.Clear();
        base.Dispose(disposing);
    }
}
