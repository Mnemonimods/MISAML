using System.Reflection;
using HarmonyLib;

namespace MISAML.Debug.Interfaces;

public interface IGodotSharp
{
    SearchResult Run(Harmony harmony, Assembly godotSharpAssembly);
}