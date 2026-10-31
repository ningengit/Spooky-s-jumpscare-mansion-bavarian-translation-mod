using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PlainAndEasyInjector;

public sealed partial class Patcher
{
    internal const string RuntimeInitFile = "RuntimeInitializeOnLoads.json";
    const string StartupClassName = "PlainAndEasyInjectorStartup";

    // RunAtGameStart hooks and AddMod types, in the order they were added, with the name used in the config's [Mods] section
    readonly List<(object Item, string Name)> startupItems = new();
    bool startupEmitted;

    // ------------------------------------------------------------------
    // Code that runs when the game starts (Unity games), like a BepInEx plugin:
    //   patcher.AddMod<Mod>();                       // Mod : MonoBehaviour. Created once at startup on its own GameObject that
    //                                                // survives scene changes, so Awake/Start/Update/OnGUI... all work.
    //   patcher.RunAtGameStart(() => Debug.Log("hi")); // any static code (no captured variables)
    // How: a small class is added to the game DLL and listed in the game's RuntimeInitializeOnLoads.json (the list Unity
    // reads at startup for [RuntimeInitializeOnLoadMethod]). It runs before the first scene loads. Each item runs
    // in its own try/catch, so one failing mod doesn't stop the others (the error goes to the game's log).
    // ------------------------------------------------------------------
    public void AddMod<TMod>() where TMod : class
    {
        var t = typeof(TMod);
        bool isMonoBehaviour = false;
        for (var b = t.BaseType; b != null && !isMonoBehaviour; b = b.BaseType) isMonoBehaviour = b.FullName == "UnityEngine.MonoBehaviour";
        if (!isMonoBehaviour) throw new ArgumentException($"{t.Name} must derive from UnityEngine.MonoBehaviour (like a BepInEx plugin).");
        if (t.IsAbstract || t.IsGenericTypeDefinition) throw new ArgumentException($"{t.Name} can't be abstract or generic.");
        Track(t);
        startupItems.Add((t, t.Name));
    }

    /// name: shown in the config's [Mods] section, where it can be switched off.
    public void RunAtGameStart(Action hook, string? name = null)
    {
        PrepareHook(hook, module);   // validates it and makes sure its assembly ships with the game
        startupItems.Add((hook, name ?? $"{hook.Method.DeclaringType?.DeclaringType?.Name ?? "Startup"}.RunAtGameStart{startupItems.Count + 1}"));
    }

    /// Adds the startup class to the main DLL (once, when saving).
    void EmitStartup()
    {
        if (startupEmitted) return;
        if (!File.Exists(Path.Combine(dataRoot, RuntimeInitFile)))
        {
            if (startupItems.Count > 0)
                throw new InvalidOperationException($"AddMod/RunAtGameStart need the game's {RuntimeInitFile} in {dataRoot} (Unity games only).");
            return;   // not a Unity game: no startup code, no log
        }
        startupEmitted = true;

        var core = resolver.TryGame("UnityEngine.CoreModule")?.MainModule
            ?? throw new InvalidOperationException($"UnityEngine.CoreModule.dll wasn't found in {dllDir}.");
        TypeDefinition UnityType(string name) => core.GetType("UnityEngine." + name) ?? throw new InvalidOperationException($"UnityEngine.{name} not found.");
        var gameObject = UnityType("GameObject");
        var unityObject = UnityType("Object");
        var debug = UnityType("Debug");
        var attribute = UnityType("RuntimeInitializeOnLoadMethodAttribute");
        var loadType = UnityType("RuntimeInitializeLoadType");

        var goCtor = module.ImportReference(gameObject.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String));
        var dontDestroy = module.ImportReference(unityObject.Methods.First(m => m.Name == "DontDestroyOnLoad" && m.Parameters.Count == 1));
        var addComponent = module.ImportReference(gameObject.Methods.First(m => m.Name == "AddComponent" && m.HasGenericParameters && m.Parameters.Count == 0));
        var logException = module.ImportReference(debug.Methods.First(m => m.Name == "LogException" && m.Parameters.Count == 1));
        var exceptionType = Map(typeof(Exception), module);

        var startup = new TypeDefinition("", StartupClassName,
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed
            | Mono.Cecil.TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
        var run = new MethodDefinition("Run", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        var attr = new CustomAttribute(module.ImportReference(attribute.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1)));
        attr.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(loadType), 1));   // BeforeSceneLoad
        run.CustomAttributes.Add(attr);
        startup.Methods.Add(run);
        module.Types.Add(startup);

        var il = run.Body.GetILProcessor();
        var end = il.Create(OpCodes.Ret);
        // first: config + log (PlainAndEasyInjector/config/PlainAndEasyInjector.cfg, PlainAndEasyInjector/LogOutput.log)
        var init = MethodRef(typeof(Logging.PaeRuntime).GetMethod(nameof(Logging.PaeRuntime.Init))!, module);
        var modEnabled = MethodRef(typeof(Logging.PaeRuntime).GetMethod(nameof(Logging.PaeRuntime.ModEnabled))!, module);
        var items = new List<(object? Item, string? Name)> { (null, null) };
        items.AddRange(startupItems.Select(s => ((object?)s.Item, (string?)s.Name)));
        foreach (var (item, name) in items)
        {
            var next = il.Create(OpCodes.Nop);
            if (name != null)
            {
                // switched off in the config? then skip it
                il.Append(il.Create(OpCodes.Ldstr, name));
                il.Append(il.Create(OpCodes.Call, modEnabled));
                il.Append(il.Create(OpCodes.Brfalse, next));
            }
            var tryStart = il.Create(OpCodes.Nop);
            il.Append(tryStart);
            if (item == null)
                il.Append(il.Create(OpCodes.Call, init));
            else if (item is Type modType)
            {
                // new GameObject("Mod").AddComponent<Mod>(), kept alive across scenes
                var addMod = new GenericInstanceMethod(addComponent);
                addMod.GenericArguments.Add(Map(modType, module));
                il.Append(il.Create(OpCodes.Ldstr, modType.Name));
                il.Append(il.Create(OpCodes.Newobj, goCtor));
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Call, dontDestroy));
                il.Append(il.Create(OpCodes.Callvirt, addMod));
                il.Append(il.Create(OpCodes.Pop));
            }
            else
            {
                var hook = PrepareHook((Delegate)item, module);
                if (hook.Singleton != null) il.Append(il.Create(OpCodes.Ldsfld, hook.Singleton));
                il.Append(il.Create(OpCodes.Call, hook.Method));
            }
            il.Append(il.Create(OpCodes.Leave, next));
            var handler = il.Create(OpCodes.Call, logException);   // the exception is on the stack
            il.Append(handler);
            il.Append(il.Create(OpCodes.Leave, next));
            il.Append(next);
            run.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = next, CatchType = exceptionType
            });
        }
        il.Append(end);
        Touch(module);
    }

    /// Writes the game's startup list with our class added.
    void StageStartupList(string staging)
    {
        if (!startupEmitted) return;
        var original = install.OriginalOf(Path.Combine(dataRoot, RuntimeInitFile));
        var json = File.ReadAllText(original);
        if (!json.Contains("\"" + StartupClassName + "\""))
        {
            var entry = "{\"assemblyName\":\"" + module.Assembly.Name.Name + "\",\"nameSpace\":\"\",\"className\":\"" + StartupClassName +
                        "\",\"methodName\":\"Run\",\"loadTypes\":1,\"isUnityClass\":false}";
            int close = json.LastIndexOf(']');
            if (close < 0) throw new InvalidOperationException($"{RuntimeInitFile} doesn't look like Unity's startup list.");
            bool empty = json.Substring(0, close).TrimEnd().EndsWith("[", StringComparison.Ordinal);
            json = json.Substring(0, close) + (empty ? "" : ",") + entry + json.Substring(close);
        }
        File.WriteAllText(Path.Combine(staging, RuntimeInitFile), json);
    }
}
