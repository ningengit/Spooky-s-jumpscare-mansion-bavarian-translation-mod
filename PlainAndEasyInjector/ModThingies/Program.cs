// Runs the Bavarian code patches on their own (normally SJSMHDBavarianPatcher does this after the asset patches).
//   PlainAndEasyInjector                         -> patch the game in place (backups are kept)
//   PlainAndEasyInjector --save <folder>         -> only write the patched files to <folder>, the game isn't touched
//   PlainAndEasyInjector --revert                -> put the original files back
//   --game "<...\SpookyUnity_Data\Managed>"      -> another game folder than BavarianMod.DefaultManagedFolder
class Program
{
    static int Main(string[] args)
    {
        string managed = BavarianMod.DefaultManagedFolder;
        string? saveTo = null;
        bool revert = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game" && i + 1 < args.Length) managed = args[++i];
            else if (args[i] == "--save" && i + 1 < args.Length) saveTo = args[++i];
            else if (args[i] == "--revert") revert = true;
        }

        if (revert)
        {
            Console.WriteLine($"Restored/removed {BavarianMod.Uninstall(managed)} file(s).");
            return 0;
        }
        bool ok = saveTo != null ? BavarianMod.SaveTo(managed, saveTo) : BavarianMod.Install(managed);
        return ok ? 0 : 1;
    }
}
