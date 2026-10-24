using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Extensions.Logging;
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
public class GodotBridgePatch(ILogger<GodotBridgePatch> logger) : IGodotBridgePatch
{

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
        var prefix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(nameof(BridgePrefix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("BridgePrefix not found!"));

        var postfix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(nameof(BridgePostfix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("BridgePostfix not found!"));

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
                logger.LogWarning($"Failed to locate {typeName}: {e.Message}");
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
                    logger.LogWarning($"Failed to patch {typeName}.{method.Name}: {e.GetType().Name}: {e.Message}");
                    totalFailed++;
                }
            }
        }

        logger.LogInformation($"complete: {totalPatched} patched across {BridgeTypeNames.Length} types, {totalFailed} failed.");
    }
    private void BridgePrefix(MethodBase __originalMethod, object[] __args, object __instance)
    {
        if (Core.Core.DebugEnabled)
        {
            try
            {
                string args = __args is { Length: > 0 }
                    ? string.Join(", ", __args.Select(a => a?.ToString() ?? "null"))
                    : "";
                logger.LogInformation($"-> {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({args})");
            }
            catch (Exception e)
            {
                logger.LogWarning("prefix logging failed: " + e.GetType().Name);
            }
        }

        try
        {
            if (__instance != null)
                OnBridgeInstanceSeen?.Invoke(__instance);
        }
        catch (Exception e)
        {
            logger.LogWarning("OnBridgeInstanceSeen subscriber threw: " + e.Message);
        }
    }

    private void BridgePostfix(MethodBase __originalMethod, object __result)
    {
        if (!Core.Core.DebugEnabled) return;

        try
        {
            string result = __result?.ToString() ?? "void";
            logger.LogInformation($"<- {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} returned {result}");
        }
        catch (Exception e)
        {
            logger.LogWarning("postfix logging failed: " + e.Message);
        }

    }
}