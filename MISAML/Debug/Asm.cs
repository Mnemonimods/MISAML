using System.Reflection;
using HarmonyLib;
using MISAML.Debug.Interfaces;

namespace MISAML.Debug;

// Every type in Asm.dll, the VM itself.
public class Asm(IDebug debug) : IAsm
{
    public SearchResult Run(Harmony harmony, Assembly asmAssembly)
        => debug.Run(harmony, Debug.AllTypesOf(asmAssembly, "Asm"), "Asm");
}