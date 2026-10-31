namespace PlainAndEasyInjector;

/// Helpers that the PATCHED game calls. They're public only because the patched DLL has to reach them;
/// you never need to call them yourself.
public static class HookRuntime
{
    /// Turns the arguments the injected code collected into the dictionary your hook receives.
    public static Dictionary<string, Argument> ToDictionary(Argument[] arguments)
    {
        var dict = new Dictionary<string, Argument>(arguments.Length);
        foreach (var a in arguments) dict[a.Name] = a;
        return dict;
    }
}
