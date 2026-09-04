using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Lists every type/method/field whose name contains the search term, in the given assembly.
internal static class NameSearch
{
    private const string GameBin = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client";
    private const string Modules = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules";

    private static int Main(string[] args)
    {
        string asmPath = args[0];
        string term = args[1].ToLowerInvariant();
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
        Assembly asm = Assembly.LoadFrom(asmPath);
        Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rex) { types = rex.Types.Where(x => x != null).ToArray(); }
        foreach (Type t in types)
        {
            bool typeHit = t.FullName.ToLowerInvariant().Contains(term);
            if (typeHit) Console.WriteLine("TYPE " + t.FullName);
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MemberInfo[] members; try { members = t.GetMethods(F).Cast<MemberInfo>().Concat(t.GetFields(F)).Concat(t.GetProperties(F)).ToArray(); } catch { continue; }
            foreach (MemberInfo m in members)
            {
                if (m.Name.ToLowerInvariant().Contains(term))
                    Console.WriteLine("  " + t.FullName + " :: " + m.MemberType + " " + m.Name);
            }
        }
        return 0;
    }
}
