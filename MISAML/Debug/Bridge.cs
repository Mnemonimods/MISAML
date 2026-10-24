using System.Linq;
using System.Reflection;
using HarmonyLib;
using MISAML.Debug.Interfaces;

namespace MISAML.Debug;

// Mnemonimov's known [GlobalClass] bridge types.
public class Bridge(Debug debug) : IBridge
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

    public SearchResult Run(Harmony harmony, Assembly mnemonimovAssembly)
        => debug.Run(harmony, TypeNames.Select(mnemonimovAssembly.GetType), "Bridge");
}