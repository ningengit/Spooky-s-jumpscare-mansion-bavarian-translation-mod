using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace PlainAndEasyInjector;

/// Read/write ANY field or property (private, protected, internal, static...) from inside a hook.
///   calc.GetMember<int>("bonus")        calc.SetMember("bonus", 5)
///   calc.CallMethod<int>("Secret", 1)   typeof(Calculator).GetStatic<int>("count")
/// Public members don't need this, just use them normally (calc.Label).
/// Numbers are converted where that's safe: an int field can be read as a long, SetMember("bonus", 5L) stores 5
/// into an int field (with a clear error if it doesn't fit), and CallMethod("Secret", 1) finds Secret(long).
public static class MemberAccess
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                           | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static readonly ConcurrentDictionary<(Type, string), MemberInfo> cache = new();

    static MemberInfo Find(Type type, string name) => cache.GetOrAdd((type, name), key =>
    {
        for (var t = key.Item1; t != null; t = t.BaseType)   // walks base classes too
        {
            if (t.GetField(name, All) is { } f) return f;
            // GetProperty(name) throws for overloaded indexers, so look for a plain (non-indexer) property by hand
            if (t.GetProperties(All).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0) is { } p) return p;
        }
        throw new MissingMemberException(key.Item1.FullName, name);
    });

    static object? Read(MemberInfo m, object? o) => m switch
    {
        FieldInfo f => f.GetValue(o),
        PropertyInfo { GetMethod: not null } p => p.GetValue(o, null),
        PropertyInfo p => throw new MemberAccessException($"Property '{p.Name}' has no getter."),
        _ => throw new NotSupportedException()
    };

    static void Write(MemberInfo m, object? o, object? value)
    {
        switch (m)
        {
            case FieldInfo f:
                SetField(f, o, ValueConverter.ConvertTo(value, f.FieldType, $"The value for '{f.Name}'"));
                break;
            case PropertyInfo p when p.SetMethod != null:
                p.SetValue(o, ValueConverter.ConvertTo(value, p.PropertyType, $"The value for '{p.Name}'"), null);
                break;
            case PropertyInfo p: // get-only auto property: write its hidden backing field
                var bf = p.DeclaringType!.GetField($"<{p.Name}>k__BackingField", All)
                         ?? throw new MemberAccessException($"Property '{p.Name}' has no setter and no backing field.");
                SetField(bf, o, ValueConverter.ConvertTo(value, bf.FieldType, $"The value for '{p.Name}'"));
                break;
        }
    }

    static void SetField(FieldInfo f, object? o, object? value)
    {
        try { f.SetValue(o, value); }
        catch (FieldAccessException ex) when (f.IsStatic && f.IsInitOnly)
        {
            // .NET Core 3+ refuses this outright; Mono/Unity allows it, but code that already ran may have the old value baked in
            throw new MemberAccessException(
                $"'{f.Name}' is static readonly and this runtime doesn't allow changing it. " +
                "Patch the value where it's assigned instead (SetInt/SetString/... on the static constructor, \".cctor\").", ex);
        }
    }

    public static V GetMember<V>(this object obj, string name) => As<V>(Read(Find(obj.GetType(), name), obj), $"Member '{name}'");
    public static void SetMember(this object obj, string name, object? value) => Write(Find(obj.GetType(), name), obj, value);
    public static V GetStatic<V>(this Type type, string name) => As<V>(Read(Find(type, name), null), $"Member '{name}'");
    public static void SetStatic(this Type type, string name, object? value) => Write(Find(type, name), null, value);

    public static V CallMethod<V>(this object obj, string name, params object?[] args) => As<V>(Invoke(obj.GetType(), obj, name, args), $"The result of '{name}'");
    public static void CallMethod(this object obj, string name, params object?[] args) => Invoke(obj.GetType(), obj, name, args);

    /// Converts instead of a plain cast, so a null or a differently sized number gives a readable error, not a crash.
    static V As<V>(object? value, string what) => (V)ValueConverter.ConvertTo(value, typeof(V), what)!;

    /// Picks the overload that fits the arguments best, the way C# would: exact types beat base classes/interfaces,
    /// which beat widening numbers (int -> long -> double). Leftover optional parameters use their default values.
    static object? Invoke(Type type, object? o, string name, object?[]? args)
    {
        args ??= new object?[] { null };   // CallMethod("X", null) means one null argument
        MethodInfo? best = null;
        object?[]? bestArgs = null;
        int bestScore = int.MaxValue, ties = 0;
        var seen = new HashSet<string>();   // overrides / hidden methods: the most derived one wins

        for (var t = type; t != null; t = t.BaseType)
            foreach (var m in t.GetMethods(All))
            {
                if (m.Name != name || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (!seen.Add(string.Join(",", ps.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)))) continue;
                if (!TryBind(ps, args, out var bound, out var score)) continue;
                if (score < bestScore) { best = m; bestArgs = bound; bestScore = score; ties = 0; }
                else if (score == bestScore) ties++;
            }

        var described = string.Join(", ", args.Select(a => a?.GetType().Name ?? "null"));
        if (best == null) throw new MissingMethodException($"No method '{name}' on {type.FullName} can take ({described}).");
        if (ties > 0) throw new AmbiguousMatchException($"Several '{name}' overloads on {type.FullName} fit ({described}) equally well. Pass arguments of the exact parameter types.");

        object? result;
        try { result = best.Invoke(best.IsStatic ? null : o, bestArgs); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();   // rethrow the method's own exception, not the reflection wrapper
            throw;
        }
        var bps = best.GetParameters();
        for (int i = 0; i < args.Length; i++)
            if (bps[i].ParameterType.IsByRef) args[i] = bestArgs![i];   // hand ref/out results back to the caller's array
        return result;
    }

    static bool TryBind(ParameterInfo[] ps, object?[] args, out object?[] bound, out int score)
    {
        bound = Array.Empty<object?>();
        score = 0;
        if (args.Length > ps.Length) return false;
        var result = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i >= args.Length)
            {
                if (!ps[i].IsOptional) return false;
                result[i] = Type.Missing;   // reflection fills in the default value
                score += 100;
                continue;
            }
            var pt = ps[i].ParameterType;
            if (pt.IsByRef) pt = pt.GetElementType()!;
            var target = Nullable.GetUnderlyingType(pt) ?? pt;
            var a = args[i];
            if (a == null)
            {
                if (pt.IsValueType && target == pt) return false;   // null can't become an int
                score += 1;
            }
            else if (a.GetType() == pt) { }
            else if (pt.IsInstanceOfType(a)) score += 1;
            else if (WideningRank(a.GetType(), target) is int rank)
            {
                a = Convert.ChangeType(a, target, CultureInfo.InvariantCulture);
                score += 10 + rank;
            }
            else return false;
            result[i] = a;
        }
        bound = result;
        return true;
    }

    // C#'s implicit numeric conversions, closest first (int prefers long over float over double)
    static readonly Dictionary<TypeCode, TypeCode[]> widening = new()
    {
        [TypeCode.SByte] = new[] { TypeCode.Int16, TypeCode.Int32, TypeCode.Int64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.Byte] = new[] { TypeCode.Int16, TypeCode.UInt16, TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.Int16] = new[] { TypeCode.Int32, TypeCode.Int64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.UInt16] = new[] { TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.Int32] = new[] { TypeCode.Int64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.UInt32] = new[] { TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.Int64] = new[] { TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.UInt64] = new[] { TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.Char] = new[] { TypeCode.UInt16, TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal },
        [TypeCode.Single] = new[] { TypeCode.Double },
    };

    static int? WideningRank(Type from, Type to)
    {
        if (from.IsEnum || to.IsEnum || !widening.TryGetValue(Type.GetTypeCode(from), out var targets)) return null;
        var i = Array.IndexOf(targets, Type.GetTypeCode(to));
        return i < 0 ? null : i;
    }
}
