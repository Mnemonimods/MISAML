using System.Reflection;
using HarmonyLib;

namespace MISAML.Interfaces;

public interface IGodotBridgePatch
{
    void TryPatch(Harmony harmony, Assembly mnemonimovAssembly);
}