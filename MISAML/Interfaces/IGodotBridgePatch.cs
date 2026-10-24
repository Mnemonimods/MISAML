using System.Reflection;
using HarmonyLib;

namespace MISAML.Interfaces;

public interface IGodotBridgePatch
{
    /// <summary>
    /// Tries to patch the Mnemonimov assembly with Harmony instance for Misaml Patches
    /// </summary>
    /// <param name="harmony">Harmony instance</param>
    /// <param name="mnemonimovAssembly">Mnemonimov's assembly</param>
    void TryPatch(Harmony harmony, Assembly mnemonimovAssembly);
}