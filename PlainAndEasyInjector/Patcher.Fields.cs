using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Globalization;

namespace PlainAndEasyInjector;

public sealed partial class Patcher
{
    // ------------------------------------------------------------------
    // Enum names
    //   patcher.SetEnumNames<PersistenceData.StateLog>("Oiwei", "Manchmoi", "Nia");
    // The names are given in the order they're written in the game's code. Only the NAMES change, not the values,
    // so saves and Unity scenes (which store the numbers) keep working. Code that turns the value into text
    // (ToString, string.Format, Enum.GetNames) shows the new names; code that parses the OLD names (Enum.Parse) won't find them.
    // ------------------------------------------------------------------
    public void SetEnumNames<TEnum>(params string[] names) where TEnum : struct, Enum => SetEnumNames(typeof(TEnum), names);

    public void SetEnumNames(Type enumType, params string[] names)
    {
        var td = TypeFor(enumType);
        var fields = EnumFields(td);
        if (names == null || names.Length != fields.Count)
            throw new ArgumentException($"{td.Name} has {fields.Count} names ({string.Join(", ", fields.Select(f => f.Name))}), but you gave {names?.Length ?? 0}.");
        if (names.Any(string.IsNullOrEmpty)) throw new ArgumentException("Enum names can't be empty.");
        var duplicate = names.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new ArgumentException($"The name \"{duplicate.Key}\" is used twice.");
        for (int i = 0; i < fields.Count; i++) fields[i].Name = names[i];
        Touch(td.Module);
    }

    /// Renames one value: patcher.SetEnumName(PersistenceData.StateLog.Never, "Nia");
    public void SetEnumName<TEnum>(TEnum value, string newName) where TEnum : struct, Enum
    {
        var td = TypeFor(typeof(TEnum));
        var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        var field = EnumFields(td).FirstOrDefault(f => Convert.ToInt64(f.Constant, CultureInfo.InvariantCulture) == number)
            ?? throw new ArgumentException($"{td.Name} has no value {number}.");
        if (string.IsNullOrEmpty(newName)) throw new ArgumentException("Enum names can't be empty.");
        if (EnumFields(td).Any(f => f != field && f.Name == newName)) throw new ArgumentException($"{td.Name} already has a value named \"{newName}\".");
        field.Name = newName;
        Touch(td.Module);
    }

    static List<FieldDefinition> EnumFields(TypeDefinition td)
    {
        if (!td.IsEnum) throw new ArgumentException($"{td.FullName} isn't an enum.");
        return td.Fields.Where(f => f.IsStatic && f.HasConstant).ToList();
    }

    // ------------------------------------------------------------------
    // Default values of fields (no instance needed): the value a field gets from the game's code.
    //   patcher.GetValueFromField<Calculator, int>("bonus")                 -> 10
    //   patcher.SetValueInField<Calculator>("bonus", 25)
    //   patcher.SetValueInField(typeof(Rewired.Consts), "unknownJoystickName", "Unbekannter Controller")
    // Works for: const fields, static fields (set in the static constructor) and instance fields (set in the
    // constructors), with values that are strings, numbers, bools, chars, enums or null. Auto-properties work by name too.
    // Good to know:
    //   - C# copies the value of a CONST into every place that uses it. For const strings those copies in the same DLL
    //     are replaced too; for const numbers they aren't (too risky: 3 is everywhere), use Set...InMethod there.
    //   - Fields that Unity saves in scenes/prefabs (public or [SerializeField] fields of MonoBehaviours etc.) get their
    //     value from the scene, not from the code: patch those with the asset patcher instead.
    // ------------------------------------------------------------------
    public V GetValueFromField<T, V>(string fieldName) => (V)ValueConverter.ConvertTo(GetValueFromField(typeof(T), fieldName), typeof(V), $"'{fieldName}'")!;

    public object? GetValueFromField(Type type, string fieldName)
    {
        var field = FindField(TypeFor(type), fieldName);
        if (field.HasConstant) return FromStored(field.Constant, field.FieldType);
        var stores = FieldStores(field);
        if (stores.Count == 0) return field.FieldType.IsValueType ? DefaultOf(field.FieldType) : null;   // never set: the default
        var literal = LiteralBefore(stores[0])
            ?? throw new InvalidOperationException($"{field.DeclaringType.Name}.{field.Name} is computed at runtime ({stores[0].Previous?.OpCode}), so there's no fixed value to read.");
        return FromStored(literal.Value, field.FieldType);
    }

    public void SetValueInField<T>(string fieldName, object? value) => SetValueInField(typeof(T), fieldName, value);

    public void SetValueInField(Type type, string fieldName, object? value)
    {
        var td = TypeFor(type);
        var field = FindField(td, fieldName);
        var stored = ToStored(value, field.FieldType, $"{td.Name}.{field.Name}");
        if (field.HasConstant && Equals(field.Constant, stored)) return;   // nothing to change (e.g. no translation yet)
        Touch(td.Module);

        if (field.HasConstant)
        {
            var old = field.Constant;
            field.Constant = stored;
            if (old is string oldText && stored is string newText)
            {
                int copies = ReplaceStringEverywhere(td.Module, oldText, newText);
                Info($"{td.Name}.{field.Name}: also replaced {copies} copies of \"{oldText}\" in {td.Module.Name}.");
            }
            else
                Warn($"{td.Name}.{field.Name} is a const: code that uses it has its own copy of {old}, which doesn't change. " +
                     "Change those with Set...InMethod in the methods that use it.");
            return;
        }

        WarnIfSavedByUnity(field);
        if (field.IsStatic) SetInitializer(StaticConstructor(td), field, stored);
        else
            foreach (var ctor in td.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.HasBody))
            {
                // a constructor that starts with ': this(...)' leaves the field to the constructor it calls
                var chained = BaseConstructorCall(ctor);
                if (chained?.Operand is MethodReference r && SameType(r.DeclaringType, td)) continue;
                SetInitializer(ctor, field, stored);
            }
    }

    // ----- helpers -----

    static FieldDefinition FindField(TypeDefinition td, string name)
    {
        var field = td.Fields.FirstOrDefault(f => f.Name == name) ?? td.Fields.FirstOrDefault(f => f.Name == $"<{name}>k__BackingField");
        if (field != null) return field;
        var property = td.Properties.FirstOrDefault(p => p.Name == name);
        throw new InvalidOperationException(property != null
            ? $"{td.Name}.{name} is a property without a stored value (its getter computes it). Change it with SetStringInMethod/... on \"get_{name}\"."
            : $"{td.Name} has no field or property '{name}'. Fields: {string.Join(", ", td.Fields.Where(f => !f.Name.StartsWith("<")).Select(f => f.Name))}");
    }

    /// Instructions that store into the field: 'stsfld' in the static constructor, 'stfld this.field' in the constructors.
    static List<Instruction> FieldStores(FieldDefinition field)
    {
        var methods = field.DeclaringType.Methods.Where(m => m.IsConstructor && m.IsStatic == field.IsStatic && m.HasBody);
        var code = field.IsStatic ? Code.Stsfld : Code.Stfld;
        return methods.SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode.Code == code && i.Operand is FieldReference fr && fr.Name == field.Name && SameType(fr.DeclaringType, field.DeclaringType))
            .ToList();
    }

    sealed class Literal { public object? Value; public Instruction First = null!; public Instruction? Conversion; }

    /// The constant loaded right before a store (ldstr / ldc.* / ldnull, or ldc.i4 + conv.i8), or null if it's computed.
    static Literal? LiteralBefore(Instruction store)
    {
        var p = store.Previous;
        if (p == null) return null;
        if (p.OpCode.Code is Code.Conv_I8 or Code.Conv_U8 && p.Previous is { } q && IntValue(q) is int small)
            return new Literal { Value = p.OpCode.Code == Code.Conv_I8 ? (long)small : (long)(uint)small, First = q, Conversion = p };
        if (IntValue(p) is int i) return new Literal { Value = i, First = p };
        return p.OpCode.Code switch
        {
            Code.Ldstr => new Literal { Value = p.Operand, First = p },
            Code.Ldnull => new Literal { Value = null, First = p },
            Code.Ldc_I8 => new Literal { Value = p.Operand, First = p },
            Code.Ldc_R4 => new Literal { Value = p.Operand, First = p },
            Code.Ldc_R8 => new Literal { Value = p.Operand, First = p },
            _ => null
        };
    }

    static int? IntValue(Instruction i) => i.OpCode.Code switch
    {
        Code.Ldc_I4 => (int)i.Operand,
        Code.Ldc_I4_S => (sbyte)i.Operand,
        Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        _ => null
    };

    void SetInitializer(MethodDefinition ctor, FieldDefinition field, object? stored)
    {
        ctor.Body.SimplifyMacros();
        var il = ctor.Body.GetILProcessor();
        var stores = ctor.Body.Instructions
            .Where(i => i.OpCode.Code == (field.IsStatic ? Code.Stsfld : Code.Stfld) && i.Operand is FieldReference fr && fr.Name == field.Name)
            .ToList();
        if (stores.Count > 0)
        {
            foreach (var store in stores)
            {
                var literal = LiteralBefore(store) ?? throw new InvalidOperationException(
                    $"{field.DeclaringType.Name}.{field.Name} is computed in {ctor.Name} (not a simple value), so it can't be replaced. " +
                    $"Use an AddAfterRun hook on \"{ctor.Name}\" with {(field.IsStatic ? "SetStatic" : "SetMember")} instead.");
                var replacement = LoadConstant(il, stored, field.FieldType);
                literal.First.OpCode = replacement.OpCode;
                literal.First.Operand = replacement.Operand;
                if (literal.Conversion != null) { literal.Conversion.OpCode = OpCodes.Nop; literal.Conversion.Operand = null; }
            }
        }
        else
        {
            // the code never sets it (it starts as 0/null): set it at the start of the constructor
            var anchor = BaseConstructorCall(ctor)?.Next ?? ctor.Body.Instructions[0];
            if (!field.IsStatic) il.InsertBefore(anchor, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(anchor, LoadConstant(il, stored, field.FieldType));
            il.InsertBefore(anchor, il.Create(field.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, field));
        }
        ctor.Body.OptimizeMacros();
    }

    static MethodDefinition StaticConstructor(TypeDefinition td)
    {
        var cctor = td.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor != null) return cctor;
        cctor = new MethodDefinition(".cctor",
            Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName
            | Mono.Cecil.MethodAttributes.RTSpecialName | Mono.Cecil.MethodAttributes.Static, td.Module.TypeSystem.Void);
        cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        td.Methods.Add(cctor);
        return cctor;
    }

    static Instruction LoadConstant(ILProcessor il, object? stored, TypeReference fieldType) => stored switch
    {
        null => il.Create(OpCodes.Ldnull),
        string s => il.Create(OpCodes.Ldstr, s),
        long l => il.Create(OpCodes.Ldc_I8, l),
        ulong ul => il.Create(OpCodes.Ldc_I8, unchecked((long)ul)),
        float f => il.Create(OpCodes.Ldc_R4, f),
        double d => il.Create(OpCodes.Ldc_R8, d),
        bool b => il.Create(OpCodes.Ldc_I4, b ? 1 : 0),
        char c => il.Create(OpCodes.Ldc_I4, (int)c),
        uint u => il.Create(OpCodes.Ldc_I4, unchecked((int)u)),
        IConvertible n => il.Create(OpCodes.Ldc_I4, Convert.ToInt32(n, CultureInfo.InvariantCulture)),   // int, short, byte, ...
        _ => throw new NotSupportedException($"Can't write a {stored.GetType().Name} into IL.")
    };

    /// Converts the value you pass to what the field stores: its own type, or for enums the underlying number.
    static object? ToStored(object? value, TypeReference fieldType, string what)
    {
        var type = StoredType(fieldType);
        if (type == null)
        {
            if (value == null && !fieldType.IsValueType) return null;
            throw new NotSupportedException($"{what} is a {fieldType.Name}. Only strings, numbers, bools, chars, enums and null can be set this way; " +
                                            "for other values use an AddAfterRun hook on the constructor with SetMember/SetStatic.");
        }
        if (value is Enum e) value = Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture);
        return ValueConverter.ConvertTo(value, type, $"The value for {what}");
    }

    /// The .NET type a field of this type is stored as, or null if it's not a simple value.
    static Type? StoredType(TypeReference t)
    {
        if (t.MetadataType == MetadataType.String) return typeof(string);
        var element = t.MetadataType == MetadataType.ValueType ? SafeResolve(t) : null;
        if (element is { IsEnum: true }) t = element.Fields.First(f => !f.IsStatic).FieldType;   // value__
        return t.MetadataType switch
        {
            MetadataType.Boolean => typeof(bool),
            MetadataType.Char => typeof(char),
            MetadataType.SByte => typeof(sbyte),
            MetadataType.Byte => typeof(byte),
            MetadataType.Int16 => typeof(short),
            MetadataType.UInt16 => typeof(ushort),
            MetadataType.Int32 => typeof(int),
            MetadataType.UInt32 => typeof(uint),
            MetadataType.Int64 => typeof(long),
            MetadataType.UInt64 => typeof(ulong),
            MetadataType.Single => typeof(float),
            MetadataType.Double => typeof(double),
            _ => null
        };
    }

    /// IL stores bools/chars/small numbers as int; turns what's stored back into the field's own type.
    static object? FromStored(object? value, TypeReference fieldType)
    {
        var type = StoredType(fieldType);
        if (value == null || type == null) return value;
        if (type == typeof(bool)) return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        if (type == typeof(char)) return (char)Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return type.IsInstanceOfType(value) ? value : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    static object? DefaultOf(TypeReference t) => StoredType(t) is { } type ? FromStored(0, t) ?? Activator.CreateInstance(type) : null;

    static int ReplaceStringEverywhere(ModuleDefinition m, string from, string to)
    {
        int count = 0;
        foreach (var method in m.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody))
            foreach (var ins in method.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == from) { ins.Operand = to; count++; }
        return count;
    }

    void WarnIfSavedByUnity(FieldDefinition field)
    {
        if (field.IsStatic || field.IsInitOnly || field.IsNotSerialized) return;
        bool serialized = field.IsPublic || field.CustomAttributes.Any(a => a.AttributeType.Name == "SerializeField");
        if (!serialized) return;
        for (var t = field.DeclaringType; t != null; t = SafeResolve(t.BaseType!))
        {
            if (t.FullName == "UnityEngine.Object")
            {
                Warn($"{field.DeclaringType.Name}.{field.Name} is saved by Unity in the game's scenes/prefabs, which overwrite the value from the code. " +
                     "If the change doesn't show up in the game, patch it with the asset patcher (SJSMHDBavarianPatcher) instead.");
                return;
            }
            if (t.BaseType == null) return;
        }
    }
}
