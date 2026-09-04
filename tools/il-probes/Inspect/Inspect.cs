using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Prints fields, properties, attributes (and enum values) of the named types.
internal static class Inspect
{
    private const string GameBin = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client";
    private const string Modules = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules";

    private static int Main(string[] args)
    {
        string[] dirs =
        {
            Path.Combine(Modules, @"BannerlordTogether\bin\Win64_Shipping_Client"),
            Path.Combine(Modules, @"Bannerlord.Harmony\bin\Win64_Shipping_Client"),
            GameBin
        };
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string d in dirs) { string p = Path.Combine(d, name); if (File.Exists(p)) return Assembly.LoadFrom(p); }
            return null;
        };
        Assembly asm = Assembly.LoadFrom(args[0]);
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (string typeName in args.Skip(1))
        {
            Type t = asm.GetType(typeName) ?? Type.GetType(typeName);
            if (t == null)
            {
                foreach (string d in dirs)
                foreach (string dll in Directory.GetFiles(d, "*.dll"))
                {
                    try { t = Assembly.LoadFrom(dll).GetType(typeName); } catch { }
                    if (t != null) break;
                }
            }
            if (t == null) { Console.WriteLine("NOT FOUND: " + typeName); continue; }
            Console.WriteLine("======== " + t.FullName + (t.IsEnum ? " (enum)" : ""));
            foreach (var a in t.GetCustomAttributesData())
                Console.WriteLine("  [attr] " + a.AttributeType.Name + "(" + string.Join(", ", a.ConstructorArguments.Select(c => c.ToString())) + ")");
            if (t.IsEnum)
            {
                foreach (string n in Enum.GetNames(t))
                    Console.WriteLine("  " + n + " = " + Convert.ToInt64(Enum.Parse(t, n)));
                continue;
            }
            foreach (MethodInfo m in t.GetMethods(F))
                Console.WriteLine("  method " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            foreach (FieldInfo f in t.GetFields(F))
                Console.WriteLine("  field " + (f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name);
            foreach (PropertyInfo p in t.GetProperties(F))
                Console.WriteLine("  prop  " + ((p.GetGetMethod(true)?.IsStatic ?? false) ? "static " : "") + p.PropertyType.Name + " " + p.Name);
        }
        return 0;
    }
}
