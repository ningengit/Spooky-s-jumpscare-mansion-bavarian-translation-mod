using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Globalization;

namespace PlainAndEasyInjector;

/// A chosen method. Hook it, or edit its literals.
public sealed class MethodSelector<T> where T : class
{
    readonly Patcher patcher;
    readonly TypeDefinition type;
    readonly string name;
    readonly string[]? args;

    internal MethodSelector(Patcher p, TypeDefinition t, string n, string[]? a = null) { patcher = p; type = t; name = n; args = a; }

    // ----- explicit overload selection (only needed if nothing below can tell the overloads apart) -----
    // Each returns a NEW selector, so a selector you keep in a variable is never changed by them.
    public MethodSelector<T> NoArgs() => Pick();
    public MethodSelector<T> Args<A1>() => Pick(typeof(A1));
    public MethodSelector<T> Args<A1, A2>() => Pick(typeof(A1), typeof(A2));
    public MethodSelector<T> Args<A1, A2, A3>() => Pick(typeof(A1), typeof(A2), typeof(A3));
    public MethodSelector<T> Args<A1, A2, A3, A4>() => Pick(typeof(A1), typeof(A2), typeof(A3), typeof(A4));
    public MethodSelector<T> Args(params Type[] types) => Pick(types);
    /// For parameter types you can't reference, e.g. ArgsByName("Game.Secret", "System.Int32").
    public MethodSelector<T> ArgsByName(params string[] fullNames) => new(patcher, type, name, fullNames);

    MethodSelector<T> Pick(params Type[] types) => new(patcher, type, name, types.Select(Patcher.CecilName).ToArray());

    static string Sig(MethodDefinition m) => $"{m.ReturnType.FullName} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName))})";

    /// Overload choice, in order: explicit Args<>() -> the argument types your hook declares -> the return type your hook uses.
    /// ref/out/in parameters match their plain type (int matches 'ref int') unless an overload matches exactly.
    MethodDefinition Resolve(Type[]? hookArgs, Type? hookResult)
    {
        var all = type.Methods.Where(m => m.Name == name && m.HasBody).ToList();
        if (all.Count == 0)
        {
            var similar = type.Methods.Where(m => m.HasBody && !m.Name.StartsWith("<") && m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Select(m => m.Name).Distinct();
            throw new InvalidOperationException($"No method '{name}' with a body on {type.FullName}." +
                (similar.Any() ? $" Did you mean: {string.Join(", ", similar)}?" : ""));
        }
        var hits = all;

        var wanted = args ?? (hookArgs is { Length: > 0 } ? hookArgs.Select(Patcher.CecilName).ToArray() : null);
        if (wanted != null)
        {
            var exact = hits.Where(m => Patcher.ParamNames(m, loose: false).SequenceEqual(wanted)).ToList();
            hits = exact.Count > 0 ? exact : hits.Where(m => Patcher.ParamNames(m, loose: true).SequenceEqual(wanted.Select(Patcher.Loose))).ToList();
        }
        if (hits.Count > 1 && hookResult != null)
            hits = hits.Where(m => m.ReturnType.FullName == Patcher.CecilName(hookResult)).ToList();

        if (hits.Count == 1) return hits[0];
        var list = string.Join("\n  ", all.Select(Sig));
        throw new InvalidOperationException(hits.Count == 0
            ? $"No overload of '{name}' matches. Available:\n  {list}"
            : $"Still ambiguous: '{name}' has several matching overloads. Declare the argument types in your hook (e.g. AddBeforeRun<int,int>) or use .Args<...>(). Available:\n  {list}");
    }

    // ----- hooks -----
    // Your hook receives: (instance, [original result,] [the method's arguments...]). Leave out what you don't need
    // (all arguments or none, or use Dictionary<string, Argument> to access specific arguments by name: args["controller"],
    // or by type: args.Get<Rewired.Controller>()).
    // Static methods get null as the instance. On constructors, before-hooks run right after the base constructor.
    // Before-hooks run in the order you add them.
    public void AddBeforeRun(Action<T> hook) => Add(hook, true, false);
    public void AddBeforeRun(Action<T, Dictionary<string, Argument>> hook) => AddDict(hook, true, false);
    public void AddBeforeRun<A1>(Action<T, A1> hook) => Add(hook, true, false);
    public void AddBeforeRun<A1, A2>(Action<T, A1, A2> hook) => Add(hook, true, false);
    public void AddBeforeRun<A1, A2, A3>(Action<T, A1, A2, A3> hook) => Add(hook, true, false);
    public void AddBeforeRun<A1, A2, A3, A4>(Action<T, A1, A2, A3, A4> hook) => Add(hook, true, false);

    // Action hooks never see the result (fine on non-void methods too: the result is left alone).
    // On iterators (Unity coroutines) and async methods, AddAfterRun(Action<T>) runs when the work FINISHES:
    // when the iteration reaches its end / right before the Task completes.
    public void AddAfterRun(Action<T> hook) => Add(hook, false, false);
    public void AddAfterRun(Action<T, Dictionary<string, Argument>> hook) => AddDict(hook, false, false);
    public void AddAfterRun<A1>(Action<T, A1> hook) => Add(hook, false, false);
    public void AddAfterRun<A1, A2>(Action<T, A1, A2> hook) => Add(hook, false, false);
    public void AddAfterRun<A1, A2, A3>(Action<T, A1, A2, A3> hook) => Add(hook, false, false);
    public void AddAfterRun<A1, A2, A3, A4>(Action<T, A1, A2, A3, A4> hook) => Add(hook, false, false);

    // Func hooks see the result and return the (possibly changed) result.
    // On iterators/async methods the result is the IEnumerator/Task itself, which you may wrap or replace.
    public void AddAfterRun<R>(Func<T, R, R> hook) => Add(hook, false, true);
    public void AddAfterRun<R>(Func<T, R, Dictionary<string, Argument>, R> hook) => AddDict(hook, false, true);
    public void AddAfterRun<R, A1>(Func<T, R, A1, R> hook) => Add(hook, false, true);
    public void AddAfterRun<R, A1, A2>(Func<T, R, A1, A2, R> hook) => Add(hook, false, true);
    public void AddAfterRun<R, A1, A2, A3>(Func<T, R, A1, A2, A3, R> hook) => Add(hook, false, true);
    public void AddAfterRun<R, A1, A2, A3, A4>(Func<T, R, A1, A2, A3, A4, R> hook) => Add(hook, false, true);

    // Change the arguments BEFORE the original runs. Write 'ref' on the parameters you want to change.
    //   .ModifyArgs<int, int>((calc, ref a, ref b) => { a = Math.Abs(a); b *= 2; });
    // ref/out parameters of the method work too (declare them by their plain type); 'in' ones get a copy.
    public void ModifyArgs<A1>(RefAction<T, A1> hook) => Add(hook, true, false, byRef: true);
    public void ModifyArgs<A1, A2>(RefAction<T, A1, A2> hook) => Add(hook, true, false, byRef: true);
    public void ModifyArgs<A1, A2, A3>(RefAction<T, A1, A2, A3> hook) => Add(hook, true, false, byRef: true);
    public void ModifyArgs<A1, A2, A3, A4>(RefAction<T, A1, A2, A3, A4> hook) => Add(hook, true, false, byRef: true);

    void AddDict(Delegate hook, bool before, bool returnsValue)
    {
        var ga = hook.GetType().GetGenericArguments();
        var result = returnsValue ? ga[1] : null;

        var m = Resolve(null, result);
        patcher.Inject(m, hook, before, returnsValue, Patcher.ArgMode.Dictionary);
    }

    void Add(Delegate hook, bool before, bool returnsValue, bool byRef = false)
    {
        // Action<T, A...>  or  Func<T, R, A..., R>  -> read the generic arguments back out
        var ga = hook.GetType().GetGenericArguments();
        var argTypes = returnsValue ? ga.Skip(2).Take(ga.Length - 3).ToArray() : ga.Skip(1).ToArray();
        var result = returnsValue ? ga[1] : null;

        var m = Resolve(argTypes, result);
        var mode = argTypes.Length == 0 ? Patcher.ArgMode.None : byRef ? Patcher.ArgMode.ByRef : Patcher.ArgMode.ByValue;
        if (mode != Patcher.ArgMode.None
            && !Patcher.ParamNames(m, loose: true).SequenceEqual(argTypes.Select(t => Patcher.Loose(Patcher.CecilName(t)))))
            throw new InvalidOperationException($"Your hook's argument types don't match {Sig(m)}.");

        patcher.Inject(m, hook, before, returnsValue, mode);
    }

    // ----- literal edits inside the method -----
    // Searched in this order: the method itself, its iterator/async body, then its lambdas and local functions.
    // which: Occurrence.All (default) = every match, First, Last, or Specific followed by match numbers (1 = first
    // match, in that order), e.g. SetIntInMethod(10, 20, Occurrence.Specific, 1, 3). Returns how many literals were changed.
    public int SetStringInMethod(string from, string to, Occurrence which = Occurrence.All, params int[] positions)
        => Patch((_, i) => i.OpCode == OpCodes.Ldstr && (string)i.Operand == from, i => i.Operand = to, which, positions,
                 $"\"{from}\"", "string literals", (_, i) => i.OpCode == OpCodes.Ldstr ? $"\"{i.Operand}\"" : null);

    public int SetIntInMethod(int from, int to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(from, to, which, positions);
    public int SetUIntInMethod(uint from, uint to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(unchecked((int)from), unchecked((int)to), which, positions);
    public int SetShortInMethod(short from, short to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(from, to, which, positions);
    public int SetUShortInMethod(ushort from, ushort to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(from, to, which, positions);
    public int SetByteInMethod(byte from, byte to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(from, to, which, positions);
    public int SetSByteInMethod(sbyte from, sbyte to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(from, to, which, positions);
    public int SetCharInMethod(char from, char to, Occurrence which = Occurrence.All, params int[] positions) => PatchI4(from, to, which, positions);

    public int SetLongInMethod(long from, long to, Occurrence which = Occurrence.All, params int[] positions)
        => Patch((_, i) => LongLiteral(i) == from, i => SetLongOperand(i, to), which, positions,
                 from + "L", "long literals", (_, i) => LongLiteral(i) is long v ? v + "L" : null);
    public int SetULongInMethod(ulong from, ulong to, Occurrence which = Occurrence.All, params int[] positions)
        => SetLongInMethod(unchecked((long)from), unchecked((long)to), which, positions);

    public int SetFloatInMethod(float from, float to, Occurrence which = Occurrence.All, params int[] positions)
        => Patch((_, i) => i.OpCode == OpCodes.Ldc_R4 && ((float)i.Operand).Equals(from), i => i.Operand = to, which, positions,
                 from.ToString("R", CultureInfo.InvariantCulture) + "f", "float literals",
                 (_, i) => i.OpCode == OpCodes.Ldc_R4 ? ((float)i.Operand).ToString("R", CultureInfo.InvariantCulture) + "f" : null);
    public int SetDoubleInMethod(double from, double to, Occurrence which = Occurrence.All, params int[] positions)
        => Patch((_, i) => i.OpCode == OpCodes.Ldc_R8 && ((double)i.Operand).Equals(from), i => i.Operand = to, which, positions,
                 from.ToString("R", CultureInfo.InvariantCulture), "double literals",
                 (_, i) => i.OpCode == OpCodes.Ldc_R8 ? ((double)i.Operand).ToString("R", CultureInfo.InvariantCulture) : null);

    public int SetEnumInMethod<E>(E from, E to, Occurrence which = Occurrence.All, params int[] positions) where E : struct, Enum
    {
        var u = Enum.GetUnderlyingType(typeof(E));
        if (u == typeof(long)) return SetLongInMethod(Convert.ToInt64(from), Convert.ToInt64(to), which, positions);
        if (u == typeof(ulong)) return SetULongInMethod(Convert.ToUInt64(from), Convert.ToUInt64(to), which, positions);
        return PatchI4(unchecked((int)Convert.ToInt64(from)), unchecked((int)Convert.ToInt64(to)), which, positions);
    }

    /// IL has no bool literal: true/false are the integers 1/0. To avoid touching real numbers, only 1/0 literals
    /// that are USED AS A BOOL count: returned from a bool method, stored in a bool local/field/parameter,
    /// or passed as the last argument to a bool parameter.
    public int SetBoolInMethod(bool from, bool to, Occurrence which = Occurrence.All, params int[] positions)
        => Patch((b, i) => IsBoolLiteral(b, i, from), i => i.Operand = to ? 1 : 0, which, positions,
                 from ? "true" : "false", "bool literals",
                 (b, i) => IsBoolLiteral(b, i, true) ? "true" : IsBoolLiteral(b, i, false) ? "false" : null);

    // ----- plumbing -----
    int PatchI4(int from, int to, Occurrence which, int[] positions)
        => Patch((b, i) => IntLiteral(b, i) == from, i => i.Operand = to, which, positions,
                 from.ToString(CultureInfo.InvariantCulture), "int literals", (b, i) => IntLiteral(b, i)?.ToString(CultureInfo.InvariantCulture));

    /// The value of a plain int literal, or null. Literals that are really bools (see SetBoolInMethod) or longs
    /// (see SetLongInMethod) don't count, so SetIntInMethod(1, ...) doesn't flip a 'return true'.
    static int? IntLiteral(Patcher.CodeBody b, Instruction i)
    {
        if (i.OpCode != OpCodes.Ldc_I4) return null;
        var v = (int)i.Operand;
        if (i.Next?.OpCode == OpCodes.Conv_I8 || i.Next?.OpCode == OpCodes.Conv_U8) return null;
        if ((v == 0 || v == 1) && IsBoolLiteral(b, i, v == 1)) return null;
        return v;
    }

    /// C# writes long values that fit into an int as an int literal plus a conversion (ldc.i4 5; conv.i8), bigger ones as ldc.i8.
    static long? LongLiteral(Instruction i)
    {
        if (i.OpCode == OpCodes.Ldc_I8) return (long)i.Operand;
        if (i.OpCode != OpCodes.Ldc_I4) return null;
        if (i.Next?.OpCode == OpCodes.Conv_I8) return (int)i.Operand;
        if (i.Next?.OpCode == OpCodes.Conv_U8) return (uint)(int)i.Operand;
        return null;
    }

    static void SetLongOperand(Instruction i, long to)
    {
        if (i.OpCode == OpCodes.Ldc_I4)
        {
            // the new value may not fit the short form: turn it into a plain ldc.i8 and drop the conversion
            i.Next.OpCode = OpCodes.Nop;
            i.Next.Operand = null;
            i.OpCode = OpCodes.Ldc_I8;
        }
        i.Operand = to;
    }

    static bool IsBoolLiteral(Patcher.CodeBody b, Instruction ins, bool value)
    {
        if (ins.OpCode != OpCodes.Ldc_I4 || (int)ins.Operand != (value ? 1 : 0)) return false;
        var next = ins.Next;
        if (next == null) return false;
        static bool IsBool(TypeReference t) => t.MetadataType == MetadataType.Boolean;
        switch (next.OpCode.Code)
        {
            case Code.Ret: return !b.IsIterator && IsBool(b.Method.ReturnType); // skip an iterator's own "has more items" returns
            case Code.Stloc: return IsBool(((VariableDefinition)next.Operand).VariableType);
            case Code.Starg: return IsBool(((ParameterDefinition)next.Operand).ParameterType);
            case Code.Stfld: case Code.Stsfld: return IsBool(((FieldReference)next.Operand).FieldType);
            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
                var ps = ((MethodReference)next.Operand).Parameters;
                return ps.Count > 0 && IsBool(ps[ps.Count - 1].ParameterType);
            default: return false;
        }
    }

    int Patch(Func<Patcher.CodeBody, Instruction, bool> match, Action<Instruction> apply, Occurrence which, int[]? positions,
              string what, string kind, Func<Patcher.CodeBody, Instruction, string?> describe)
    {
        positions ??= Array.Empty<int>();
        CheckOccurrence(which, positions);
        var method = Resolve(null, null);
        var bodies = Patcher.BodiesOf(method);
        foreach (var b in bodies) b.Method.Body.SimplifyMacros(); // ldc.i4.0 / ldc.i4.s / ... -> plain ldc.i4, so matching is uniform
        try
        {
            var found = new List<Instruction>();
            foreach (var b in bodies)
                foreach (var ins in b.Method.Body.Instructions)
                    if (match(b, ins)) found.Add(ins);

            if (found.Count == 0)
            {
                // say what IS there: e.g. an interpolated string is stored in pieces ("Hello ", "!") or as a template ("Hello {0}!")
                var present = bodies.SelectMany(b => b.Method.Body.Instructions.Select(i => describe(b, i)))
                                    .Where(s => s != null).Distinct().Take(30).ToList();
                throw new InvalidOperationException($"{what} not found in {type.Name}.{name}. " +
                    (present.Count == 0 ? $"It has no {kind}." : $"Its {kind}: {string.Join(", ", present)}"));
            }

            var chosen = which switch
            {
                Occurrence.First => new List<Instruction> { found[0] },
                Occurrence.Last => new List<Instruction> { found[found.Count - 1] },
                Occurrence.Specific => positions.Select(p => p <= found.Count ? found[p - 1]
                    : throw new ArgumentOutOfRangeException(nameof(positions),
                        $"You asked for match #{p} of {what}, but {type.Name}.{name} only has {found.Count}.")).ToList(),
                _ => found
            };
            foreach (var ins in chosen) apply(ins);
            patcher.Touch(method.Module);
            return chosen.Count;
        }
        finally
        {
            foreach (var b in bodies) b.Method.Body.OptimizeMacros();
        }
    }

    static void CheckOccurrence(Occurrence which, int[] positions)
    {
        if (!Enum.IsDefined(typeof(Occurrence), which)) throw new ArgumentOutOfRangeException(nameof(which));
        if (which != Occurrence.Specific)
        {
            if (positions.Length > 0) throw new ArgumentException($"Match numbers only work with Occurrence.Specific (you used Occurrence.{which}).");
            return;
        }
        if (positions.Length == 0) throw new ArgumentException("Occurrence.Specific needs the numbers of the matches to change, e.g. SetIntInMethod(10, 20, Occurrence.Specific, 1, 3).");
        if (positions.Any(p => p < 1)) throw new ArgumentException("Match numbers start at 1 (1 = the first match).");
        if (positions.Distinct().Count() != positions.Length) throw new ArgumentException("The same match number is listed twice.");
    }
}
