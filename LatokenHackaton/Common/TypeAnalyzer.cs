using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

internal static class TypeAnalyzer
{
    private static readonly Dictionary<int, OpCode> OpCodeMap = BuildOpCodeMap();

    public static string AnalyzeType(Type t)
    {
        var doc = new
        {
            TypeName = t.FullName,
            Fields = t.GetFields(AllBf()).Select(x => x.Name).ToList(),
            Constructors = t.GetConstructors(AllBf()).Select(AnalyzeCtor).ToList(),
            Methods = t.GetMethods(AllBf()).Select(AnalyzeMethod).ToList(),
            Properties = t.GetProperties(AllBf()).Select(AnalyzeProperty).ToList()
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object AnalyzeCtor(ConstructorInfo c)
    {
        var il = Disassemble(c);
        var baseCallIdx = il.FindIndex(i =>
            (i.OpCode == "call" || i.OpCode == "callvirt") && IsCtorCall(i.Token, c.Module));
        var fieldInits = new List<object>();
        var prefix = baseCallIdx < 0 ? il : il.Take(baseCallIdx);

        foreach (var instr in prefix)
        {
            if (instr.OpCode == "stfld" || instr.OpCode == "stsfld")
            {
                fieldInits.Add(new
                {
                    Field = ResolveFieldName(instr.Token, c.Module),
                    Instr = instr.ToString()
                });
            }
        }

        return new
        {
            c.Name,
            IL = il.Select(x => x.ToString()).ToList(),
            FieldInits = fieldInits
        };
    }

    private static object AnalyzeMethod(MethodInfo m)
    {
        var il = Disassemble(m);
        return new
        {
            m.Name,
            IL = il.Select(x => x.ToString()).ToList()
        };
    }

    private static object AnalyzeProperty(PropertyInfo p)
    {
        var g = p.GetGetMethod(true);
        var s = p.GetSetMethod(true);

        return new
        {
            p.Name,
            GetterIL = g != null ? Disassemble(g).Select(x => x.ToString()).ToList() : null,
            SetterIL = s != null ? Disassemble(s).Select(x => x.ToString()).ToList() : null
        };
    }

    private static List<ILInstr> Disassemble(MethodBase mb)
    {
        var body = mb.GetMethodBody();
        if (body == null)
            return new List<ILInstr> { new ILInstr(0, "no-body") };

        var code = body.GetILAsByteArray();
        if (code == null || code.Length == 0)
            return new List<ILInstr> { new ILInstr(0, "empty-body") };

        var list = new List<ILInstr>();
        var pos = 0;

        while (pos < code.Length)
        {
            var offset = pos;
            var b = code[pos++];
            int key;

            if (b == 0xFE)
            {
                if (pos >= code.Length)
                {
                    list.Add(new ILInstr(offset, "invalid-fe"));
                    break;
                }
                var b2 = code[pos++];
                key = 0xFE00 | b2;
            }
            else
            {
                key = b;
            }

            if (!OpCodeMap.TryGetValue(key, out var op))
            {
                list.Add(new ILInstr(offset, $"unknown_{b:X2}"));
                continue;
            }

            var size = 0;
            switch (op.OperandType)
            {
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineBrTarget:
                    size = 1;
                    break;
                case OperandType.InlineI:
                case OperandType.InlineVar:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineBrTarget:
                    size = 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    size = 8;
                    break;
                case OperandType.ShortInlineR:
                    size = 4;
                    break;
            }

            byte[] raw = null;
            int? token = null;
            string operand = string.Empty;

            if (size > 0)
            {
                if (pos + size > code.Length)
                {
                    list.Add(new ILInstr(offset, $"invalid_operand_{op.Name}"));
                    break;
                }

                raw = new byte[size];
                Buffer.BlockCopy(code, pos, raw, 0, size);
                pos += size;

                if (IsToken(op.OperandType))
                {
                    token = BitConverter.ToInt32(raw, 0);
                    operand = $"TOKEN_0x{token.Value:X8}";
                }
                else
                {
                    operand = DecodeOperand(op.OperandType, raw);
                }
            }

            list.Add(new ILInstr(offset, op.Name, operand, token));
        }

        return list;
    }

    private static bool IsCtorCall(int? tk, Module m)
    {
        if (!tk.HasValue) return false;
        try
        {
            var mb = m.ResolveMethod(tk.Value);
            return mb is ConstructorInfo;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveFieldName(int? tk, Module m)
    {
        if (!tk.HasValue) return "<unknown>";
        try
        {
            var f = m.ResolveField(tk.Value);
            return f != null ? f.Name : "<unknown>";
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static bool IsToken(OperandType t) =>
        t == OperandType.InlineField
        || t == OperandType.InlineMethod
        || t == OperandType.InlineType
        || t == OperandType.InlineTok
        || t == OperandType.InlineString
        || t == OperandType.InlineSig;

    private static string DecodeOperand(OperandType t, byte[] raw)
    {
        switch (t)
        {
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
            case OperandType.ShortInlineBrTarget:
                return ((sbyte)raw[0]).ToString();
            case OperandType.InlineI:
            case OperandType.InlineVar:
            case OperandType.InlineBrTarget:
                return BitConverter.ToInt32(raw, 0).ToString();
            case OperandType.InlineI8:
                return BitConverter.ToInt64(raw, 0).ToString();
            case OperandType.InlineR:
                return BitConverter.ToDouble(raw, 0).ToString();
            case OperandType.ShortInlineR:
                return BitConverter.ToSingle(raw, 0).ToString();
            default:
                return string.Empty;
        }
    }

    private static BindingFlags AllBf() =>
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private static Dictionary<int, OpCode> BuildOpCodeMap()
    {
        var dict = new Dictionary<int, OpCode>();
        var fields = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public);

        foreach (var f in fields)
        {
            if (f.FieldType == typeof(OpCode))
            {
                var oc = (OpCode)f.GetValue(null)!;
                var val = (ushort)oc.Value;
                var lo = val & 0xFF;
                var hi = (val >> 8) & 0xFF;
                var key = (hi == 0xFE) ? (0xFE00 | lo) : lo;

                if (!dict.ContainsKey(key))
                    dict[key] = oc;
            }
        }
        return dict;
    }

    private sealed class ILInstr
    {
        public int Offset { get; }
        public string OpCode { get; }
        public string Operand { get; }
        public int? Token { get; }

        public ILInstr(int offset, string opCode, string operand = "", int? token = null)
        {
            Offset = offset;
            OpCode = opCode ?? "";
            Operand = operand ?? "";
            Token = token;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Operand))
                return $"IL_{Offset:X4}: {OpCode} {Operand}";
            return $"IL_{Offset:X4}: {OpCode}";
        }
    }
}