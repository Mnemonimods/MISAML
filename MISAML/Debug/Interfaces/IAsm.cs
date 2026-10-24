using System.Reflection;
using HarmonyLib;

namespace MISAML.Debug.Interfaces;

public interface IAsm
{
    SearchResult Run(Harmony harmony, Assembly asmAssembly);
}