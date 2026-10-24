using System;
using System.Collections.Generic;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap.Interfaces;

public interface IModSettings
{
    /// <summary>
    /// Loads the settings of a mod
    /// </summary>
    /// <param name="path">the path of the settings</param>
    /// <returns>Returns a K-V pairing of mod-id and their settings</returns>
    public Dictionary<string, ModSetting>? Load(string path);
}