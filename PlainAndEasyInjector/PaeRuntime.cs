using UnityEngine;

namespace PlainAndEasyInjector.Logging;

/// Runs inside the patched game at startup, before any mod: reads the config and sets up the log.
public static class PaeRuntime
{
    static bool initialized;
    static ConfigFile? config;
    static readonly ModLogger log = new("PlainAndEasyInjector");
    [ThreadStatic] static bool sendingToUnity;

    /// <game folder>/PlainAndEasyInjector: config and LogOutput.log.
    public static string Folder { get; private set; } = "";

    public static void Init()
    {
        if (initialized) return;
        initialized = true;

        string data = Application.dataPath;
        string gameFolder = Path.GetDirectoryName(data) ?? data;
        Folder = Path.Combine(gameFolder, "PlainAndEasyInjector");
        config = new ConfigFile(Path.Combine(Folder, "config", "PlainAndEasyInjector.cfg"));

        bool unityListening = config.Get("Logging", "UnityLogListening", true, "Shows Unity's own log messages (Debug.Log, errors, exceptions) in the PlainAndEasyInjector log.");
        bool toUnity = config.Get("Logging", "LogToUnityLog", true, "Also writes the messages of your mods into Unity's log (Player.log).");
        bool consoleOn = config.Get("Logging.Console", "Enabled", false, "Shows the log in a console window next to the game (Windows). On Linux/macOS it goes to standard output (start the game from a terminal to see it).");
        bool preventClose = config.Get("Logging.Console", "PreventClose", false, "Removes the console window's close button. Closing that window would also close the game.");
        var consoleLevels = config.Get("Logging.Console", "LogLevels", LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info, "Which log levels are shown on the console.");
        bool diskOn = config.Get("Logging.Disk", "Enabled", true, "Writes the log to PlainAndEasyInjector/LogOutput.log.");
        bool append = config.Get("Logging.Disk", "AppendLog", false, "Appends to the log file instead of starting a new one at every game start.");
        bool unityToDisk = config.Get("Logging.Disk", "WriteUnityLog", true, "Includes Unity's log messages in the log file.");
        var diskLevels = config.Get("Logging.Disk", "LogLevels", LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info, "Which log levels are written to the log file.");
        config.SaveIfChanged();

        bool consoleWindow = consoleOn && LogConsole.Open($"{Application.productName} - PlainAndEasyInjector log", preventClose);
        PaeLog.Configure(diskOn ? Path.Combine(Folder, "LogOutput.log") : null, append, diskLevels, consoleOn, consoleLevels, unityToDisk);

        if (toUnity)
            PaeLog.ToUnity = (level, text) =>
            {
                sendingToUnity = true;
                try
                {
                    if (level <= LogLevel.Error) Debug.LogError(text);
                    else if (level == LogLevel.Warning) Debug.LogWarning(text);
                    else Debug.Log(text);
                }
                finally { sendingToUnity = false; }
            };

        if (unityListening)
            Application.logMessageReceivedThreaded += (message, stackTrace, type) =>
            {
                if (sendingToUnity) return;   // our own message, already in the log
                var level = type switch
                {
                    LogType.Exception => LogLevel.Error,
                    LogType.Error or LogType.Assert => LogLevel.Error,
                    LogType.Warning => LogLevel.Warning,
                    _ => LogLevel.Info,
                };
                var text = type == LogType.Exception && !string.IsNullOrEmpty(stackTrace) ? message + "\nStack trace:\n" + stackTrace.TrimEnd() : message;
                PaeLog.FromUnity(level, text);
            };

        if (consoleOn) log.Info(consoleWindow ? "Console window opened" : "No console window possible here; the console log goes to standard output");
        log.Message($"PlainAndEasyInjector {typeof(PaeRuntime).Assembly.GetName().Version} - {Application.productName} (Unity {Application.unityVersion}, {Application.platform}) - {PaeLog.Stamp()}");
    }

    /// Whether a mod is switched on in the config ([Mods] section). New mods are added as "true".
    public static bool ModEnabled(string name)
    {
        if (config == null) return true;
        bool on = config.Get("Mods", name, true, $"Switches the mod {name} on or off (no new patching needed).");
        config.SaveIfChanged();
        if (!on) log.Info($"{name} is switched off in the config");
        return on;
    }
}
