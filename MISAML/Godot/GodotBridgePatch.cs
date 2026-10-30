using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MISAML.Interfaces;

namespace MISAML.Godot;

// The single mod-facing patching thing, this is very much WIP and WILL 
// be changed in the near future. A mod names any type+method it wants
// ("Asm.Running.VirtualMachine", "HandleSystemCall", or anything else)
// and patches, lazily, on first registration.

// MISAML's own bootstrap stuff, not mod-facing, patches all of Mnemonimov's
// known [GlobalClass] bridge types so Loader can fetch a live GodotObject
// and reach the isolated AssemlyLoadContext. How to find this list:
//   ilspycmd Mnemonimov.dll -o decompiled_mnemonimov -p
//   grep -rl "GlobalClass" decompiled_mnemonimov/
public class GodotBridgePatch() : IGodotBridgePatch
{
    private static ILogger _logger = NullLogger.Instance;

    public GodotBridgePatch(ILogger<GodotBridgePatch> logger) : this()
    {
        _logger = logger;
    }

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

    private static readonly string[] BridgeTypeNames =
    [
        "Mnemonimov.src.asm.VirtualMachineRunner",
        "Mnemonimov.src.asm.ShellLexer",
        "Mnemonimov.src.asm.AssemblerRunner",
        "Mnemonimov.src.asm.TextSearcher",
        "Mnemonimov.src.asm.CsUtils",
    ];

    // raised with the live instance on every patched bridge call,
    // how Loader gets a real GodotObject to bootstrap the isolated
    // context.
    public static event Action<object>? OnBridgeInstanceSeen;

    public void TryPatch(Harmony harmony, Assembly mnemonimovAssembly)
    {
        // logging needs the call's args and result, which makes Harmony allocate
        // on every call, even when nothing gets logged, according to profiler,
        // so... the logging versions are only patched in when debug is on,
        // otherwise a light prefix that only needs the instance
        var debug = Core.Core.DebugEnabled;

        var prefix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(debug ? nameof(BridgePrefixDebug) : nameof(BridgePrefix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("bridge prefix not found!"));

        var postfix = debug
            ? new HarmonyMethod(
                typeof(GodotBridgePatch).GetMethod(nameof(BridgePostfix),
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("BridgePostfix not found!"))
            : null;

        int totalPatched = 0;
        int totalFailed = 0;

        foreach (var typeName in BridgeTypeNames)
        {
            Type bridgeType;
            try
            {
                bridgeType = mnemonimovAssembly.GetType(typeName)
                             ?? throw new Exception($"{typeName} not found");
            }
            catch (Exception e)
            {
                _logger.LogWarning($"Failed to locate {typeName}: {e.Message}");
                continue;
            }

            var methods = bridgeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (SkipNames.Contains(method.Name)) continue;
                if (harmony.GetPatchedMethods().Contains(method)) continue;

                var usePostfix = method.ReturnType != typeof(void) ? postfix : null;

                try
                {
                    harmony.Patch(method, prefix: prefix, postfix: usePostfix);
                    totalPatched++;
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"Failed to patch {typeName}.{method.Name}: {e.GetType().Name}: {e.Message}");
                    totalFailed++;
                }
            }
        }

        _logger.LogInformation($"complete: {totalPatched} patched across {BridgeTypeNames.Length} types, {totalFailed} failed.");
    }
    private static void BridgePrefix(object __instance) => RaiseInstanceSeen(__instance);

    private static void BridgePrefixDebug(MethodBase __originalMethod, object[] __args, object __instance)
    {
        try
        {
            string args = __args is { Length: > 0 }
                ? string.Join(", ", __args.Select(a => a?.ToString() ?? "null"))
                : "";
            _logger.LogInformation($"-> {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({args})");
        }
        catch (Exception e)
        {
            _logger.LogWarning("prefix logging failed: " + e.GetType().Name);
        }

        RaiseInstanceSeen(__instance);
    }

    private static void RaiseInstanceSeen(object? instance)
    {
        try
        {
            if (instance != null)
                OnBridgeInstanceSeen?.Invoke(instance);
        }
        catch (Exception e)
        {
            _logger.LogWarning("OnBridgeInstanceSeen subscriber threw: " + e.Message);
        }
    }

    // only patched in when debug is on
    private static void BridgePostfix(MethodBase __originalMethod, object __result)
    {
        try
        {
            string result = __result?.ToString() ?? "void";
            _logger.LogInformation($"<- {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} returned {result}");
        }
        catch (Exception e)
        {
            _logger.LogWarning("postfix logging failed: " + e.Message);
        }
    }
}