using System;

namespace MISAML.Interfaces;

// what mods can actually do with IDs, only Register* nothing that can close regs
public interface IIds
{
    bool RegisterSyscall(string word, Action<object> handler);
    bool RegisterConstant(string word, long value);
    bool RegisterConstant(string word, float value);
}