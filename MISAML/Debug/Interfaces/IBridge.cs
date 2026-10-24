using System.Reflection;
using HarmonyLib;

namespace MISAML.Debug.Interfaces;

public interface IBridge
{
    SearchResult Run(Harmony harmony, Assembly mnemonimovAssembly);
}