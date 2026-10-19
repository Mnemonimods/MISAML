using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace MISAML.Debug;

internal static class Log
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(Log).Assembly.Location)!,
        "misaml-debug.log"
    );

    private static bool _clr = false;

    public static void Write(string l)
    {
        if (!_clr)
        {
            File.WriteAllText(LogPath, "");
            _clr = true;
        }
        File.AppendAllText(LogPath, l + "\n");
    }
}

// struct for results of search, readonly means immutable, record means data focused!
public readonly record struct SearchResult(
    int TypesScanned,
    int Patchable,
    int Skipped,
    int Failed
);

// shared by bridge/asm/mnemonimov/godotsharp!!! the reflection + probe then unpatch
// logic is here! each method is patched just long enough so harmony can see if it
// can make IL for it! then we unpatch, just a one time discovery thingy!!
internal static class Search
{

    // How the bridge list below was found:
    //
    // This is worth repeating after every game update, since class and method
    // names can change between builds.
    //
    // 1. Decompiled Mnemonimov.dll with ilspycmd:
    //
    //      ilspycmd Mnemonimov.dll -o decompiled_mnemonimov -p
    //
    //    Mnemonimov.dll is located in data_Mnemonimov_linuxbsd_x86_64/, next to
    //    the game executable.
    //
    // 2. Searched the decompiled C# source for every class that Godot exposes
    //    as a scriptable bridge:
    //
    //      grep -rl "GlobalClass" decompiled_mnemonimov/
    //
    // 3. Cross-reference this by searching for classes that inherit
    //    directly from Godot base types. This catches bridge classes even if they
    //    don't use the [GlobalClass] attribute:
    //
    //      grep -rln ": RefCounted\|: Node\|: Resource\|: GodotObject\|: Control" decompiled_mnemonimov/
    //
    // 4. Checked the actual namespace of every bridge class with:
    //
    //      grep -n "^namespace" <each file>
    //
    //    DO NOT assume that the namespace matches the directory structure as it often
    //    does not.
    //
    // As of the build this was last checked against, all 5 bridge classes are
    //    in the same namespace:
    //
    //      Mnemonimov.src.asm
    //
    // This is true regardless of which directory their individual .cs files
    // are located in.
    //
    // These are Godot's own interop override methods, these take Godot's 
    // internal by reference types (godot_string_name, godot_variant, etc.) and 
    // will throw InvalidProgramException when Harmony tries to rewrite them.
    //
    // We skip anything matching these method names.
    //
    // How these were identified:
    //
    // Check every method in each bridge class's decompiled source and
    // looked for methods marked "protected override" or "internal static"
    // whose signatures contain godot_string_name, godot_variant, or
    // NativeVariantPtrArgs. These consistently correspond to Godot's
    // compiler generated interop code rather than real game logic.
    //
    // "InvokeGodotClassStaticMethod" is the same kind of Godot interop
    // method, but it only appears on classes that expose static bridge
    // methods (AssemblerRunner and CsUtils). It is internal, so the
    // BindingFlags filter already excludes it. The entry is kept
    // anyway to pick up potential false positives.
    private static readonly HashSet<string> SkipNames =
    [
        "InvokeGodotClassMethod",
        "HasGodotClassMethod",
        "SetGodotClassPropertyValue",
        "GetGodotClassPropertyValue",
        "SaveGodotObjectData",
        "RestoreGodotObjectData",
        "GetGodotMethodList",
        "GetGodotPropertyList",
        "InvokeGodotClassStaticMethod",
    ];

    // remember, array of Godot Type things!!!!
    public static Type[] AllTypesOf(Assembly assembly, string label)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            /* we skip any null types!!! take them out of array! */
            return e.Types.Where(t => t != null).ToArray()!;
        }
    }

    public static SearchResult Run(
        Harmony harmony, IEnumerable<Type?> types, string label
    /* enumerable because it's an array! */
    )
    {
        // maybe can just be ToArray()!,.,,??? i think... eepy..
        var typeList = types.Where(t => t != null).Select(t => t!).ToArray();

        int ok = 0, skipped = 0, failed = 0;
        int typesScanned = typeList.Length;

        IEnumerable<(Type, MethodInfo)> AllMethods()
        {
            foreach (var type in typeList)
            {
                // array of MethodInfo :)
                MethodInfo[] methods;
                try
                {
                    // get all methods on this type matching the flags!!
                    methods = type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.DeclaredOnly);
                }
                catch
                {
                    // just skip if it fails and move to next type!!!
                    continue;
                }

                foreach (var method in methods)
                    // yield so we get all methods for each type!!!!
                    yield return (type, method);
            }
        }

        ProbeAll(harmony, AllMethods(), label, ref ok, ref skipped, ref failed);

        return new SearchResult(typesScanned, ok, skipped, failed);
    }

    // same thing as Run(types), but for an exact, already known set
    // of methods instead of every method on every given type.
    public static SearchResult RunMethods(Harmony harmony, IEnumerable<MethodInfo> methods, string label)
    {
        var methodList = methods.ToArray();

        int ok = 0, skipped = 0, failed = 0;

        ProbeAll(harmony, methodList.Select(m => (m.DeclaringType!, m)), label, ref ok, ref skipped, ref failed);

        return new SearchResult(methodList.Length, ok, skipped, failed);
    }

    // the probe, patch, log, unpatched loop, shared by Run and RunMethods.
    private static void ProbeAll(
        Harmony harmony,
        IEnumerable<(Type type, MethodInfo method)> methods,
        string label,
        ref int ok,
        ref int skipped,
        ref int failed
    )
    {
        // find the patch methods and wrap them for harmony (confused me...)
        var prefix = new HarmonyMethod(
            typeof(Search).GetMethod(nameof(ProbePrefix), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("ProbePrefix not found!"));

        var postfix = new HarmonyMethod(
            typeof(Search).GetMethod(nameof(ProbePostfix), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("ProbePostfix not found!"));

        foreach (var (type, method) in methods)
        {
            var sig = Signature(type, method);

            // skips abstract and open generic stuff!!!!
            if (method.IsAbstract || method.ContainsGenericParameters)
            {
                Log.Write($"SKIP|{label}|{sig}|abstract or open-generic!");
                skipped++;
                continue;
            }

            // one of our skipname ones!!!
            if (SkipNames.Contains(method.Name))
            {
                Log.Write($"SKIP|{label}|{sig}|Godot interop override!");
                skipped++;
                continue;
            }

            // getpatchedmethods returns IEnumerable<MethodBase> containing
            // the original methods that harmony has patched!!!
            if (harmony.GetPatchedMethods().Contains(method))
            {
                Log.Write($"SKIP|{label}|{sig}|already patched elsewhere!");
                continue;
            }

            // skips native only entry points!!!
            if (method.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == "UnmanagedCallersOnlyAttribute"))
            {
                Log.Write($"SKIP|{label}|{sig}|UnmanagedCallersOnly, native only!");
                skipped++;
                continue;
            }

            // if return type not void, we use postfix! if void, use null!!!
            var usePostfix = method.ReturnType != typeof(void)
                ? postfix
                : null;

            // attempt patch on method! if it passes, log it and ok!
            try
            {
                harmony.Patch(method, prefix: prefix, postfix: usePostfix);
                Log.Write($"OK|{label}|{sig}|");
                ok++;
            }
            catch (Exception e)
            {
                Log.Write($"FAIL|{label}|{sig}|{e.GetType().Name}: {e.Message}");
                failed++;
                continue;
            }

            try
            {
                harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            }
            catch (Exception e)
            {
                Log.Write($"WARN|{label}|{sig}|patched but failed to unpatch: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    // visibility, static/instance/extension, special-name/inlining tags,
    // return type, declaring type, method name, every parameters
    // type+name!!!
    private static string Signature(Type type, MethodInfo method)
    {
        var visibility =
            method.IsPublic ? "public" :
            method.IsPrivate ? "private" :
            method.IsAssembly ? "internal" :
            method.IsFamily ? "protected" :
            method.IsFamilyOrAssembly ? "protected internal" :
            method.IsFamilyAndAssembly ? "private protected" :
            "?";

        var isExtension =
            method.IsStatic && method.IsDefined(typeof(ExtensionAttribute), false);

        var binding =
            method.IsStatic
                ? (isExtension ? "static extension" : "static")
                : "instance";

        var tags = new List<string>();

        var specialNameTag = SpecialNameTag(method);
        if (specialNameTag != null)
            tags.Add(specialNameTag);

        if ((method.GetMethodImplementationFlags() &
                MethodImplAttributes.AggressiveInlining) != 0)
        {
            tags.Add("agressive-inlining");
        }

        var tagString = string.Join(",", tags);

        var parameters = string.Join(", ",
            method.GetParameters()
                .Select(p => $"{p.ParameterType.Name} {p.Name}"));

        var name = $"{type.FullName}.{method.Name}";
        var signature = $"{name}({parameters})";
        var returnType = method.ReturnType.Name;

        return $"{visibility}|{binding}|{tagString}|{returnType}|{signature}";
    }

    // separates "this is actually a property/operator/event accessor"
    // from an ordinary method of the same compiler generated shape.
    private static string? SpecialNameTag(MethodInfo method)
    {
        if (!method.IsSpecialName)
            return null;

        var name = method.Name;

        if (name.StartsWith("get_", StringComparison.Ordinal) ||
            name.StartsWith("set_", StringComparison.Ordinal))
            return "property";

        if (name.StartsWith("add_", StringComparison.Ordinal) ||
            name.StartsWith("remove_", StringComparison.Ordinal))
            return "event";

        if (name.StartsWith("op_", StringComparison.Ordinal))
            return "operator";

        return "special-name";
    }

    // no-op probes, to unpatch immediately after, never fire these.
    private static bool ProbePrefix(MethodBase __originalMethod, object[] __args) => true;
    private static void ProbePostfix(MethodBase __originalMethod, object __result) { }
}

// Mnemonimov's known [GlobalClass] bridge types.
public static class Bridge
{
    // This is a complete, confirmed list 
    // based on the discovery method documented.
    //
    // As of the build this was checked against, these are also all of the
    // game's .cs files: 6 files total, with 5 being bridge classes and the
    // remaining one being the MatchMode enum (so it isn't a bridge).
    //
    // This was confirmed with:
    //
    // find decompiled_mnemonimov extracted -name "*.cs" | sort -u
    private static readonly string[] TypeNames =
    [
        "Mnemonimov.src.asm.VirtualMachineRunner",
        "Mnemonimov.src.asm.ShellLexer",
        "Mnemonimov.src.asm.AssemblerRunner",
        "Mnemonimov.src.asm.TextSearcher",
        "Mnemonimov.src.asm.CsUtils",
    ];

    public static SearchResult Run(Harmony harmony, Assembly mnemonimovAssembly)
        => Search.Run(harmony, TypeNames.Select(mnemonimovAssembly.GetType), "Bridge");
}

// Every type in Asm.dll, the VM itself.
public static class Asm
{
    public static SearchResult Run(Harmony harmony, Assembly asmAssembly)
        => Search.Run(harmony, Search.AllTypesOf(asmAssembly, "Asm"), "Asm");
}

// Every type in Mnemonimov.dll, including (and beyond) the bridge types.
public static class Mnemonimov
{
    public static SearchResult Run(Harmony harmony, Assembly mneminomovAssembly)
        => Search.Run(harmony, Search.AllTypesOf(mneminomovAssembly, "Mnemonimov"), "Mnemonimov");
}

// Every type in GodotSharp.dll, public and private, it's the whole engine API,
// not entirely needed and very large. Docs on this will be limited.
public static class GodotSharp
{
    // Found via: monodis --output=X.il <dll>, then
    //   rg -o '\[GodotSharp\][\w.]+::\w+' mnemonimov.il asm.il --no-filename | sort -u
    // against the live Mnemonimov.dll/Asm.dll. Redo this after a game
    // update, same idea as Bridge.TypeNames. 6 of the 26 raw matches
    // are already in Search's SkipNames (Godot's own interop overrides,
    // always InvalidProgramException) and are dropped from this list, however
    // they can still be included.
    private static readonly (string typeName, string methodName)[] UsedMethods =
    [
        ("Godot.Bridge.GodotSerializationInfo", "AddProperty"),
        ("Godot.Bridge.GodotSerializationInfo", "TryGetProperty"),
        ("Godot.Collections.Dictionary", "Add"),
        ("Godot.GodotObject", "Dispose"),
        ("Godot.Image", "CreateFromData"),
        ("Godot.Image", "SetData"),
        ("Godot.ImageTexture", "CreateFromImage"),
        ("Godot.ImageTexture", "Update"),
        ("Godot.NativeInterop.NativeVariantPtrArgs", "get_Count"),
        ("Godot.NativeInterop.NativeVariantPtrArgs", "get_Item"),
        ("Godot.NativeInterop.VariantUtils", "ConvertTo"),
        ("Godot.NativeInterop.VariantUtils", "ConvertToDictionary"),
        ("Godot.NativeInterop.VariantUtils", "CreateFrom"),
        ("Godot.NativeInterop.VariantUtils", "CreateFromArray"),
        ("Godot.NativeInterop.VariantUtils", "CreateFromDictionary"),
        ("Godot.StringName", "op_Equality"),
        ("Godot.StringName", "op_Implicit"),
        ("Godot.Variant", "As"),
        ("Godot.Variant", "From"),
        ("Godot.Variant", "op_Implicit"),
    ];

    public static SearchResult Run(Harmony harmony, Assembly godotSharpAssembly)
        => Search.RunMethods(harmony, ResolveMethods(godotSharpAssembly), "GodotSharp");

    // multiple overloads can share a name, funcs with same name diff params...
    // (e.g. VarianUtils.CreateFrom<T>), the IL match only gave the name,
    // so resolve every overload instead of guessing which one
    private static IEnumerable<MethodInfo> ResolveMethods(Assembly godotSharpAssembly)
    {
        // go through every type + method pair we want to find!!!
        foreach (var (typeName, methodName) in UsedMethods)
        {
            var type = godotSharpAssembly.GetType(typeName);
            if (type == null)
            {
                Log.Write($"SKIP|GodotSharp|{typeName}.{methodName}|type not found, game update likely renamed/removed it");
                continue;
            }

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            // go through every method and only keep methods with the name we want!!!
            foreach (var method in methods.Where(m => m.Name == methodName))
                /* gives this method back to whoever called ResolveMethods() */
                /* yield pauses here and continues from here when the next */
                /* method is requested. it allows the function to produce */
                /* multiple results, per each method. as we can find multiple */
                /* matching methods per type. */
                yield return method;
        }
    }
}