using System;
using System.Collections.Generic;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap.Interfaces;

public interface IModSettings
{
    public Dictionary<string, ModSetting>? Load(string path, Action<string> log);
}