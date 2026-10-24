using System.Reflection;
using HarmonyLib;
using MISAML.Debug.Interfaces;

namespace MISAML.Debug;

// Every type in Mnemonimov.dll, including (and beyond) the bridge types.

public class Mnemonimov(IDebug search) : IMnemonimov
{
    public SearchResult Run(Harmony harmony, Assembly mneminomovAssembly)
        => search.Run(harmony, Debug.AllTypesOf(mneminomovAssembly, "Mnemonimov"), "Mnemonimov");
}