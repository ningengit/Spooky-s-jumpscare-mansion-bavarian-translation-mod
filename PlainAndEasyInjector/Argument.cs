using System.Globalization;

namespace PlainAndEasyInjector;

/// Wraps a method argument passed into a dictionary-based hook.
public sealed class Argument
{
    public string Name { get; }
    /// The parameter's declared type (for ref/out/in parameters: the type behind the reference). Null if unknown.
    public Type? Type { get; }
    public object? Value { get; private set; }
    public bool IsModified { get; private set; }
    /// 'in' parameters can be read but not changed.
    public bool IsReadOnly { get; }

    public Argument(string name, object? value) : this(name, value, null, false) { }

    public Argument(string name, object? value, Type? type, bool isReadOnly)
    {
        Name = name;
        Value = value;
        Type = type;
        IsReadOnly = isReadOnly;
    }

    /// Reads the value as T. Numbers are converted when needed (an int argument can be read as a long).
    public T GetArgValue<T>() => (T)ValueConverter.ConvertTo(Value, typeof(T), $"Argument '{Name}'")!;

    /// Changes the value. It is converted to the parameter's type when possible (e.g. 5L for an int parameter);
    /// otherwise you get a clear error here instead of a crash inside the game.
    public void SetArgValue<T>(T value)
    {
        if (IsReadOnly) throw new InvalidOperationException($"Argument '{Name}' is an 'in' parameter; it can't be changed.");
        Value = Type == null ? value : ValueConverter.ConvertTo(value, Type, $"The new value for argument '{Name}'");
        IsModified = true;
    }
}

/// Shortcuts for the dictionary a hook receives:
///   var controller = args.Get<Rewired.Controller>();   // the only argument of that type: no name needed
///   args.Set("Gamer");                                  // changes the only string argument (before-hooks)
public static class ArgumentExtensions
{
    public static T Get<T>(this Dictionary<string, Argument> args) => ByType(args, typeof(T)).GetArgValue<T>();
    public static T Get<T>(this Dictionary<string, Argument> args, string name) => ByName(args, name).GetArgValue<T>();
    public static void Set<T>(this Dictionary<string, Argument> args, T value) => ByType(args, typeof(T)).SetArgValue(value);
    public static void Set<T>(this Dictionary<string, Argument> args, string name, T value) => ByName(args, name).SetArgValue(value);

    static Argument ByName(Dictionary<string, Argument> args, string name)
        => args.TryGetValue(name, out var a) ? a
         : throw new KeyNotFoundException($"There's no argument '{name}'. Arguments: {Describe(args)}");

    /// The argument declared as exactly T, else the only one T can hold (a base class or interface of it).
    static Argument ByType(Dictionary<string, Argument> args, Type t)
    {
        var exact = args.Values.Where(a => a.Type == t).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new InvalidOperationException($"Several arguments are {t.Name}s ({string.Join(", ", exact.Select(a => a.Name))}): use the name, e.g. args.Get<{t.Name}>(\"{exact[0].Name}\").");
        var fitting = args.Values.Where(a => a.Type != null && t.IsAssignableFrom(a.Type)).ToList();
        if (fitting.Count == 1) return fitting[0];
        throw new InvalidOperationException(fitting.Count == 0
            ? $"No argument is a {t.Name}. Arguments: {Describe(args)}"
            : $"Several arguments fit {t.Name} ({string.Join(", ", fitting.Select(a => a.Name))}): use the name.");
    }

    static string Describe(Dictionary<string, Argument> args) => string.Join(", ", args.Values.Select(a => $"{a.Type?.Name ?? "?"} {a.Name}"));
}

/// Shared conversion rules for Argument and MemberAccess.
internal static class ValueConverter
{
    /// Returns 'value' as an instance of 'target': as-is if it already fits, otherwise via numeric/enum conversion.
    /// Throws a readable exception if that isn't possible. 'what' describes the value for the error message.
    public static object? ConvertTo(object? value, Type target, string what)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (value == null)
        {
            if (!target.IsValueType || underlying != null) return null;
            throw new InvalidCastException($"{what} is null, which isn't a valid {target.Name}.");
        }
        if (target.IsInstanceOfType(value)) return value;

        var to = underlying ?? target;
        var from = value.GetType();
        try
        {
            if (from.IsEnum) { value = Convert.ChangeType(value, Enum.GetUnderlyingType(from), CultureInfo.InvariantCulture); from = value.GetType(); }
            if (to.IsEnum && IsNumber(from)) return Enum.ToObject(to, value);
            if (IsNumber(to) && IsNumber(from)) return Convert.ChangeType(value, to, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new InvalidCastException($"{what} ({value}) doesn't fit into {to.Name}.");
        }
        catch (InvalidCastException)
        {
            // e.g. char <-> double: reported below like any other mismatch
        }
        throw new InvalidCastException($"{what} is a {from.Name}, which can't be used as a {target.Name}.");
    }

    internal static bool IsNumber(Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
            or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal or TypeCode.Char => !t.IsEnum,
        _ => false
    };
}
