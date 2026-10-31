using AssetsTools.NET;

namespace SJSMHDBavarianPatcher;

public sealed class ModException : Exception
{
    public ModException(string message) : base(message) { }
}

/// <summary>
/// One modification: "in file X, asset with path ID Y, change field Z".
/// File paths are relative to the game's *_Data folder (e.g. "sharedassets1.assets" or "StreamingAssets/mansionrooms").
/// For a bundle, pass the assets file inside it as <c>inner:</c> (e.g. "BuildPlayer-DefaultMainMenu.sharedAssets").
/// Field paths use dots, array elements are numbers: "m_Resource.m_Source", "m_Items.Array.0.m_Name".
/// </summary>
public sealed class Mod
{
    public string GameFile { get; init; } = "";
    public string? Inner { get; init; }
    public long PathId { get; init; }
    public string Field { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Gets the field node and changes it (getter and setter in one).</summary>
    public Action<AssetTypeValueField>? Apply { get; init; }

    /// <summary>Set for audio replacements: path of the WAV file (relative to this program).</summary>
    public string? Wav { get; init; }

    /// <summary>Set for texture replacements: path of the PNG file (relative to this program).</summary>
    public string? Png { get; init; }

    // ---------- typed setters (optional 'expected' = abort if the current value is different) ----------

    public static Mod SetString(string file, long pathId, string field, string value, string? expected = null, string? inner = null)
        => Typed(file, inner, pathId, field, value, expected, f => f.AsString, (f, v) => f.AsString = v);

    public static Mod SetInt(string file, long pathId, string field, int value, int? expected = null, string? inner = null)
        => Typed<long>(file, inner, pathId, field, value, expected.HasValue ? (object)(long)expected.Value : null,
                       FieldNumbers.Get, FieldNumbers.Set);

    public static Mod SetLong(string file, long pathId, string field, long value, long? expected = null, string? inner = null)
        => Typed<long>(file, inner, pathId, field, value, expected, FieldNumbers.Get, FieldNumbers.Set);

    public static Mod SetULong(string file, long pathId, string field, ulong value, ulong? expected = null, string? inner = null)
        => Typed(file, inner, pathId, field, value, expected, f => f.AsULong, (f, v) => f.AsULong = v);

    public static Mod SetFloat(string file, long pathId, string field, float value, float? expected = null, string? inner = null)
        => Typed(file, inner, pathId, field, value, expected, f => f.AsFloat, (f, v) => f.AsFloat = v);

    public static Mod SetDouble(string file, long pathId, string field, double value, double? expected = null, string? inner = null)
        => Typed(file, inner, pathId, field, value, expected, f => f.AsDouble, (f, v) => f.AsDouble = v);

    public static Mod SetBool(string file, long pathId, string field, bool value, bool? expected = null, string? inner = null)
        => Typed(file, inner, pathId, field, value, expected, f => f.AsBool, (f, v) => f.AsBool = v);

    // ---------- free-form edit: full access to the field (read it, compute, write it) ----------

    public static Mod Edit(string file, long pathId, string field, Action<AssetTypeValueField> edit, string? inner = null)
        => new Mod
        {
            GameFile = file, Inner = inner, PathId = pathId, Field = field,
            Description = $"{Where(file, inner)} #{pathId} {field} (custom edit)",
            Apply = edit
        };

    // ---------- audio replacement (16-bit PCM WAV, mono or stereo) ----------

    public static Mod Audio(string file, long pathId, string wavPath, string? inner = null)
        => new Mod
        {
            GameFile = file, Inner = inner, PathId = pathId, Wav = wavPath,
            Description = $"{Where(file, inner)} #{pathId} audio <- {wavPath}"
        };

    // ---------- texture replacement (PNG, stored uncompressed as RGBA32) ----------

    public static Mod Texture2D(string file, long pathId, string pngPath, string? inner = null)
        => new Mod
        {
            GameFile = file, Inner = inner, PathId = pathId, Png = pngPath,
            Description = $"{Where(file, inner)} #{pathId} texture <- {pngPath}"
        };

    // ---------- helpers ----------

    private static string Where(string file, string? inner) => inner == null ? file : $"{file} > {inner}";

    private static Mod Typed<T>(string file, string? inner, long pathId, string field, T value, object? expected,
                                Func<AssetTypeValueField, T> get, Action<AssetTypeValueField, T> set)
        => new Mod
        {
            GameFile = file, Inner = inner, PathId = pathId, Field = field,
            Description = $"{Where(file, inner)} #{pathId} {field} = {value}",
            Apply = f =>
            {
                if (expected != null && !Equals(get(f), expected))
                    throw new ModException($"{field} is '{get(f)}' but '{expected}' was expected (different game version?)");
                set(f, value);
            }
        };
}

/// <summary>Reads and writes integer fields according to their real Unity type (int, unsigned int, 64-bit).</summary>
public static class FieldNumbers
{
    public static long Get(AssetTypeValueField f)
    {
        switch (f.TemplateField.Type)
        {
            case "unsigned int": return f.AsUInt;
            case "UInt64":
            case "FileSize": return (long)f.AsULong;
            case "SInt64": return f.AsLong;
            default: return f.AsInt;
        }
    }

    public static void Set(AssetTypeValueField f, long value)
    {
        switch (f.TemplateField.Type)
        {
            case "unsigned int": f.AsUInt = checked((uint)value); break;
            case "UInt64":
            case "FileSize": f.AsULong = checked((ulong)value); break;
            case "SInt64": f.AsLong = value; break;
            default: f.AsInt = checked((int)value); break;
        }
    }
}
