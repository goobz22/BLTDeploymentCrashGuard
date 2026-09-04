internal static class VerCheck {
    private static void Main(string[] a) {
        System.Console.WriteLine(System.Reflection.AssemblyName.GetAssemblyName(a[0]).FullName);
    }
}
