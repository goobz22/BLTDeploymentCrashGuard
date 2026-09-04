using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// Disassembles named methods of BannerlordTogether.dll (offsets, opcodes, resolved
// tokens, branch targets) so the livelock's control flow can be read directly.
internal static class IlDump
{
    private const string GameBin = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client";
    private const string Modules = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules";

    private static readonly OpCode[] One = new OpCode[0x100];
    private static readonly OpCode[] Two = new OpCode[0x100];

    private static int Main(string[] args)
    {
        foreach (FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public))
        {
            OpCode op = (OpCode)fi.GetValue(null);
            if (op.Size == 1) One[op.Value & 0xFF] = op; else Two[op.Value & 0xFF] = op;
        }
        string[] dirs =
        {
            Path.Combine(Modules, @"BannerlordTogether\bin\Win64_Shipping_Client"),
            Path.Combine(Modules, @"Bannerlord.Harmony\bin\Win64_Shipping_Client"),
            GameBin
        };
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string d in dirs)
            {
                string p = Path.Combine(d, name);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
        string target = args.Length > 0 && args[0].EndsWith(".dll") ? args[0] : Path.Combine(dirs[0], "BannerlordTogether.dll");
        if (args.Length > 0 && args[0].EndsWith(".dll")) { args = args.Skip(1).ToArray(); }
        Assembly bt = Assembly.LoadFrom(target);

        // args: TypeName::MethodName pairs; default set = the livelock suspects.
        string[] targets = args.Length > 0 ? args : new[]
        {
            "BannerlordTogether.CoopSubModule::TryBackgroundCampaignTick",
            "BannerlordTogether.CoopSubModule::OnApplicationTick",
        };
        foreach (string t in targets)
        {
            string[] parts = t.Split(new[] { "::" }, StringSplitOptions.None);
            Type type = bt.GetType(parts[0]);
            if (type == null) { Console.WriteLine("TYPE NOT FOUND: " + parts[0]); continue; }
            var methods = new System.Collections.Generic.List<System.Reflection.MethodBase>();
            if (parts[1] == ".cctor" || parts[1] == "cctor") { var ci = type.TypeInitializer; if (ci != null) methods.Add(ci); }
            else if (parts[1] == ".ctor" || parts[1] == "ctor") { methods.AddRange(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)); }
            else methods.AddRange(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == parts[1]));
            if (methods.Count == 0) { Console.WriteLine("METHOD NOT FOUND: " + t); continue; }
            foreach (MethodBase m in methods) Disasm(m);
        }
        return 0;
    }

    private static void Disasm(MethodBase m)
    {
        Console.WriteLine("======== " + m.DeclaringType.FullName + "." + m.Name + " (" +
            string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ") -> " + ((m as MethodInfo)?.ReturnType.Name ?? "void/ctor"));
        MethodBody body = m.GetMethodBody();
        if (body == null) { Console.WriteLine("  (no body)"); return; }
        foreach (LocalVariableInfo lv in body.LocalVariables)
            Console.WriteLine("  local " + lv.LocalIndex + ": " + lv.LocalType.Name);
        byte[] il = body.GetILAsByteArray();
        Module mod = m.Module;
        Type[] tGen = m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
        Type[] mGen = m.IsGenericMethod ? m.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            int offset = i;
            OpCode op;
            if (il[i] == 0xFE) { op = Two[il[i + 1]]; i += 2; } else { op = One[il[i]]; i += 1; }
            string operand = "";
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: operand = "-> IL_" + (i + 1 + (sbyte)il[i]).ToString("X4"); i += 1; break;
                case OperandType.InlineBrTarget: operand = "-> IL_" + (i + 4 + BitConverter.ToInt32(il, i)).ToString("X4"); i += 4; break;
                case OperandType.ShortInlineI: operand = ((sbyte)il[i]).ToString(); i += 1; break;
                case OperandType.InlineI: operand = BitConverter.ToInt32(il, i).ToString(); i += 4; break;
                case OperandType.InlineI8: operand = BitConverter.ToInt64(il, i).ToString(); i += 8; break;
                case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, i).ToString(); i += 4; break;
                case OperandType.InlineR: operand = BitConverter.ToDouble(il, i).ToString(); i += 8; break;
                case OperandType.ShortInlineVar: operand = "V_" + il[i]; i += 1; break;
                case OperandType.InlineVar: operand = "V_" + BitConverter.ToInt16(il, i); i += 2; break;
                case OperandType.InlineString:
                    try { operand = "\"" + mod.ResolveString(BitConverter.ToInt32(il, i)) + "\""; } catch { operand = "(str?)"; }
                    i += 4; break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    try
                    {
                        MemberInfo mb = mod.ResolveMember(BitConverter.ToInt32(il, i), tGen, mGen);
                        operand = (mb.DeclaringType != null ? mb.DeclaringType.FullName + "::" : "") + mb.Name;
                    }
                    catch (Exception ex) { operand = "(tok? " + ex.GetType().Name + ")"; }
                    i += 4; break;
                case OperandType.InlineSwitch:
                    int n = BitConverter.ToInt32(il, i); i += 4;
                    var ts = new List<string>();
                    for (int k = 0; k < n; k++) { ts.Add("IL_" + (i + 4 * (n - k) + BitConverter.ToInt32(il, i)).ToString("X4")); i += 4; }
                    operand = "[" + string.Join(", ", ts) + "]";
                    break;
                case OperandType.InlineSig: i += 4; operand = "(sig)"; break;
                default: Console.WriteLine("  UNKNOWN OPERAND TYPE " + op.OperandType); return;
            }
            Console.WriteLine("  IL_" + offset.ToString("X4") + ": " + op.Name.PadRight(12) + " " + operand);
        }
        Console.WriteLine();
    }
}
