using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Finds every method in an assembly whose IL calls a member whose declaring-type+name contains the term.
internal static class Callers
{
    private const string GameBin = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client";
    private const string Modules = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules";
    private static int Main(string[] args)
    {
        string[] dirs = { Path.Combine(Modules, @"BannerlordTogether\bin\Win64_Shipping_Client"), Path.Combine(Modules, @"SandBox\bin\Win64_Shipping_Client"), Path.Combine(Modules, @"Bannerlord.Harmony\bin\Win64_Shipping_Client"), GameBin };
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { string n = new AssemblyName(e.Name).Name + ".dll"; foreach (string d in dirs) { string p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); } return null; };
        Assembly asm = Assembly.LoadFrom(args[0]);
        string term = args[1];
        Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (Type t in types)
        {
            MethodBase[] methods; try { methods = t.GetMethods(F).Cast<MethodBase>().Concat(t.GetConstructors(F)).ToArray(); } catch { continue; }
            foreach (MethodBase m in methods)
            {
                MethodBody body; try { body = m.GetMethodBody(); } catch { continue; }
                if (body == null) continue;
                byte[] il = body.GetILAsByteArray();
                for (int i = 0; i < il.Length - 4; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F) continue;
                    try
                    {
                        MemberInfo mb = m.Module.ResolveMember(BitConverter.ToInt32(il, i + 1), t.IsGenericType ? t.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null);
                        string full = (mb.DeclaringType != null ? mb.DeclaringType.FullName : "") + "::" + mb.Name;
                        if (full.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) { Console.WriteLine(t.FullName + "::" + m.Name + "  ->  " + full); i += 4; }
                    }
                    catch { }
                }
            }
        }
        return 0;
    }
}
