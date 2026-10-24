using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MISAML.Debug.Interfaces;

public interface IDebug
{
    public SearchResult Run(
        Harmony harmony, IEnumerable<Type?> types, string label);

    public SearchResult RunMethods(Harmony harmony, IEnumerable<MethodInfo> methods, string label);
}