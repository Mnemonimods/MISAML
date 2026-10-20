using System;
using System.Runtime.Loader;

namespace MISAML.Interfaces;

public interface IModLoader
{
    /// <summary>
    /// Loads mods into Mnemonimov
    /// </summary>
    /// <param name="modsDir">the directory where all the mods are stored</param>
    /// <param name="log">logger</param>
    void LoadMods(string modsDir, Action<string> log);

    /// <summary>
    /// Mnemonimov's assembly load context
    /// </summary>
    AssemblyLoadContext? GameContext { get; set; }
}