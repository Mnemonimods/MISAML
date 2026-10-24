using System.Reflection;
using HarmonyLib;

namespace MISAML.Debug.Interfaces;

public interface IMnemonimov
{
    SearchResult Run(Harmony harmony, Assembly mneminomovAssembly);
}