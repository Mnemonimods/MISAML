using System;
using System.Runtime.Loader;

namespace MISAML.Interfaces;

public interface IModLoader
{
    void LoadMods(string modsDir, Action<string> log);

    AssemblyLoadContext? GameContext { get; set; }
}