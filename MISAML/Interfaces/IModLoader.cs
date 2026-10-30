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
    void LoadMods(string modsDir);

    /// <summary>
    /// Mnemonimov's assembly load context
    /// </summary>
    AssemblyLoadContext? GameContext { get; set; }

    /// <summary>
    /// Hands out the IDs mods registered and writes them into the game. Runs once,
    /// later Call it after every mod has loaded and before the game's first lex
    /// or assemble call.
    /// </summary>
    void AllocateIds();
}