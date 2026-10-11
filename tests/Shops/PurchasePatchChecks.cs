using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class PatchSmoke
{
    private static string[] roots;
    public static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        roots = new[] { Path.GetDirectoryName(args[0]), Path.GetDirectoryName(args[1]), args[2] };
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { return Run(args[0], args[1]); }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var root in roots)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    private static MethodInfo Method(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        if (method == null) throw new MissingMethodException(type.FullName, name);
        Console.WriteLine("METHOD " + type.FullName + "." + method);
        return method;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string radioPath, string nativePath)
    {
        const string owner = "com.skeptic043.sailwind.radio.localpurchasesmoke";
        Console.WriteLine("CLR " + Environment.Version);
        Console.WriteLine("Harmony " + typeof(Harmony).Assembly.FullName);
        var radio = Assembly.LoadFrom(radioPath);
        var native = Assembly.LoadFrom(nativePath);
        Console.WriteLine("Radio " + radio.FullName);
        Console.WriteLine("Native " + native.FullName);
        var nativePrefab = native.GetType("SaveablePrefab", true);
        var capture = Method(nativePrefab, "PrepareSaveData");
        var load = Method(nativePrefab, "Load");
        var world = radio.GetType("SailwindRadio.Physical.RadioWorldService", true);
        var harmony = new Harmony(owner);
        try
        {
            harmony.Patch(capture, prefix: new HarmonyMethod(Method(world, "CapturePrefix")));
            Verify(capture, owner, 1, 0, 0);
            harmony.Patch(load, prefix: new HarmonyMethod(Method(world, "PrefabLoadPrefix")),
                postfix: new HarmonyMethod(Method(world, "PrefabPostfix")));
            Verify(load, owner, 1, 1, 0);
            Console.WriteLine("PASS native capture and restoration wrappers constructed");
        }
        finally
        {
            harmony.Unpatch(capture, HarmonyPatchType.All, owner);
            harmony.Unpatch(load, HarmonyPatchType.All, owner);
            Verify(capture, owner, 0, 0, 0);
            Verify(load, owner, 0, 0, 0);
            Console.WriteLine("PASS owned test patches removed");
        }
        Console.WriteLine("PASS construction only. No game method or Radio service was invoked. No Unity gameplay acceptance.");
        return 0;
    }
    private static void Verify(MethodBase target, string owner, int prefix, int postfix, int finalizer)
    {
        var info = Harmony.GetPatchInfo(target);
        int p = info == null ? 0 : info.Prefixes.Count(x => x.owner == owner);
        int q = info == null ? 0 : info.Postfixes.Count(x => x.owner == owner);
        int f = info == null ? 0 : info.Finalizers.Count(x => x.owner == owner);
        if (p != prefix || q != postfix || f != finalizer)
            throw new InvalidOperationException("Unexpected owned patches " + p + "/" + q + "/" + f);
    }
}
