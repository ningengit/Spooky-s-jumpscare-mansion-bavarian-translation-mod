using System.Globalization;
using System.Text;

namespace PlainAndEasyInjector.Logging;

/// Log levels, like BepInEx's.
[Flags]
public enum LogLevel
{
    None = 0,
    Fatal = 1,
    Error = 2,
    Warning = 4,
    Message = 8,
    Info = 16,
    Debug = 32,
    All = Fatal | Error | Warning | Message | Info | Debug,
}

/// A simple INI file like BepInEx.cfg: [Section], "## description" lines, "Key = Value".
/// Missing entries are added with their default value and description, so the file documents itself.
public sealed class ConfigFile
{
    readonly string path;
    readonly List<(string Section, string Key, string Value, string Description)> entries = new();
    readonly Dictionary<string, string> loaded = new(StringComparer.OrdinalIgnoreCase);
    bool changed;

    public string FilePath => path;

    public ConfigFile(string path)
    {
        this.path = path;
        if (!File.Exists(path)) { changed = true; return; }
        string section = "";
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            loaded[section + "\n" + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
    }

    public string Get(string section, string key, string defaultValue, string description)
    {
        var id = section + "\n" + key;
        if (!loaded.TryGetValue(id, out var value)) { value = defaultValue; changed = true; }
        entries.RemoveAll(e => e.Section == section && e.Key == key);
        entries.Add((section, key, value, description));
        return value;
    }

    public bool Get(string section, string key, bool defaultValue, string description)
    {
        var text = Get(section, key, defaultValue ? "true" : "false", description + "\n# Setting type: Boolean\n# Default value: " + (defaultValue ? "true" : "false"));
        return bool.TryParse(text, out var b) ? b : defaultValue;
    }

    public LogLevel Get(string section, string key, LogLevel defaultValue, string description)
    {
        var text = Get(section, key, Describe(defaultValue), description +
            "\n# Setting type: LogLevel\n# Default value: " + Describe(defaultValue) +
            "\n# Acceptable values: None, Fatal, Error, Warning, Message, Info, Debug, All\n# Multiple values can be set at the same time by separating them with , (e.g. Debug, Warning)");
        return ParseLevels(text, defaultValue);
    }

    /// Writes the file if entries were added (keeps values the user changed).
    public void SaveIfChanged()
    {
        if (!changed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder();
            foreach (var group in entries.GroupBy(e => e.Section))
            {
                sb.Append('[').Append(group.Key).AppendLine("]").AppendLine();
                foreach (var e in group)
                {
                    foreach (var d in e.Description.Split('\n'))
                        sb.AppendLine(d.StartsWith("#", StringComparison.Ordinal) ? d : "## " + d);
                    sb.Append(e.Key).Append(" = ").AppendLine(e.Value).AppendLine();
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            changed = false;
        }
        catch { /* a read-only game folder: the defaults are used */ }
    }

    static string Describe(LogLevel l)
        => l == LogLevel.All ? "All" : l == LogLevel.None ? "None"
         : string.Join(", ", new[] { LogLevel.Fatal, LogLevel.Error, LogLevel.Warning, LogLevel.Message, LogLevel.Info, LogLevel.Debug }.Where(x => (l & x) != 0));

    static LogLevel ParseLevels(string text, LogLevel fallback)
    {
        LogLevel result = LogLevel.None;
        foreach (var part in text.Split(','))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (!Enum.TryParse(p, true, out LogLevel l)) return fallback;
            result |= l;
        }
        return result;
    }
}

/// A named log source for your mod, like BepInEx's ManualLogSource:
///   static readonly ModLogger Log = new ModLogger("Bavaria");
///   Log.Info("Servus!");    ->  [Info   :Bavaria] Servus!
public sealed class ModLogger
{
    public string Source { get; }
    public ModLogger(string source) => Source = source;

    public void Fatal(object? message) => PaeLog.Write(LogLevel.Fatal, Source, message);
    public void Error(object? message) => PaeLog.Write(LogLevel.Error, Source, message);
    public void Warning(object? message) => PaeLog.Write(LogLevel.Warning, Source, message);
    public void Message(object? message) => PaeLog.Write(LogLevel.Message, Source, message);
    public void Info(object? message) => PaeLog.Write(LogLevel.Info, Source, message);
    public void Debug(object? message) => PaeLog.Write(LogLevel.Debug, Source, message);
    public void Log(LogLevel level, object? message) => PaeLog.Write(level, Source, message);
}

/// The central log: everything goes to the disk log (PlainAndEasyInjector/LogOutput.log) and the console,
/// each filtered by its own levels from the config.
public static class PaeLog
{
    static readonly object gate = new();
    static StreamWriter? disk;
    static LogLevel diskLevels = LogLevel.All, consoleLevels = LogLevel.All;
    static bool consoleEnabled, writeUnityLogToDisk, writeUnityLogToConsole = true;
    static readonly List<string> early = new();   // messages before Init

    /// Mod messages are passed on to Unity's log (Player.log / Debug.Log) too, if set.
    internal static Action<LogLevel, string>? ToUnity;
    [ThreadStatic] static bool writingFromUnity;

    internal static void Configure(string? logFile, bool append, LogLevel disk, bool console, LogLevel consoleLevels, bool unityToDisk)
    {
        lock (gate)
        {
            diskLevels = disk;
            PaeLog.consoleLevels = consoleLevels;
            consoleEnabled = console;
            writeUnityLogToDisk = unityToDisk;
            if (logFile != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                    PaeLog.disk = new StreamWriter(new FileStream(logFile, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch { PaeLog.disk = null; }
            }
            foreach (var line in early) Emit(LogLevel.Info, line, fromUnity: false);
            early.Clear();
        }
    }

    public static void Write(LogLevel level, string source, object? message)
    {
        var text = message?.ToString() ?? "null";
        var line = $"[{level,-7}:{source,10}] {text}";
        lock (gate)
        {
            if (disk == null && !consoleEnabled && early.Count < 1000) early.Add(line);
            Emit(level, line, fromUnity: false);
        }
        if (!writingFromUnity && ToUnity != null && level != LogLevel.Debug)
            try { ToUnity(level, $"[{source}] {text}"); } catch { /* the log must never break the game */ }
    }

    /// A message from Unity's own log (Debug.Log, errors, exceptions).
    internal static void FromUnity(LogLevel level, string text)
    {
        writingFromUnity = true;
        try
        {
            lock (gate) Emit(level, $"[{level,-7}:{"Unity Log",10}] {text}", fromUnity: true);
        }
        finally { writingFromUnity = false; }
    }

    static void Emit(LogLevel level, string line, bool fromUnity)
    {
        if (disk != null && (diskLevels & level) != 0 && (!fromUnity || writeUnityLogToDisk))
            try { disk.WriteLine(line); } catch { }
        if (consoleEnabled && (consoleLevels & level) != 0 && (!fromUnity || writeUnityLogToConsole))
            try { LogConsole.WriteLine(level, line); } catch { }
    }

    internal static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
