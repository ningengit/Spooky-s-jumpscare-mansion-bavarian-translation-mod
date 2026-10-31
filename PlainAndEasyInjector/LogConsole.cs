using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlainAndEasyInjector.Logging;

/// A console window for the log on Windows (like BepInEx's), opened by the game itself when
/// [Logging.Console] Enabled = true. On Linux/macOS the log goes to standard output instead
/// (visible when the game is started from a terminal).
static class LogConsole
{
    const int StdOutputHandle = -11;
    const uint ScClose = 0xF060, MfByCommand = 0x0;

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool SetConsoleOutputCP(uint codePage);
    [DllImport("kernel32.dll")] static extern bool SetConsoleTextAttribute(IntPtr handle, ushort attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetConsoleTitle(string title);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll")] static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    static StreamWriter? writer;
    static IntPtr handle;

    public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// Opens the window. Returns false if that isn't possible (then standard output is used).
    public static bool Open(string title, bool preventClose)
    {
        if (!IsWindows) return false;
        try
        {
            AllocConsole();   // false if the game already has one: then we just use it
            handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            SetConsoleOutputCP(65001);   // UTF-8, so ä ö ü ß show correctly
            SetConsoleTitle(title);
            if (preventClose)
            {
                // closing the console window would end the game, so its close button can be removed
                var menu = GetSystemMenu(GetConsoleWindow(), false);
                if (menu != IntPtr.Zero) DeleteMenu(menu, ScClose, MfByCommand);
            }
            writer = new StreamWriter(new FileStream(new SafeFileHandle(handle, false), FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
            return true;
        }
        catch
        {
            writer = null;
            return false;
        }
    }

    public static void WriteLine(LogLevel level, string line)
    {
        if (writer == null)
        {
            Console.WriteLine(line);
            return;
        }
        // FOREGROUND_* bits: 1 blue, 2 green, 4 red, 8 bright
        ushort color = level switch
        {
            LogLevel.Fatal => 4 | 8,
            LogLevel.Error => 4 | 8,
            LogLevel.Warning => 4 | 2 | 8,
            LogLevel.Message => 4 | 2 | 1 | 8,
            LogLevel.Debug => 8,
            _ => 4 | 2 | 1,
        };
        SetConsoleTextAttribute(handle, color);
        writer.WriteLine(line);
        SetConsoleTextAttribute(handle, 4 | 2 | 1);
    }
}
