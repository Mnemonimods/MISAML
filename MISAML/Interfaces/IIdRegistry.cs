using System;
using System.Collections.Generic;
using MISAML.Core.Records;

namespace MISAML.Interfaces;

public interface IIdRegistry
{
    // a syscall: gets a word, a token and a code, the handler gets the running VM
    bool RegisterSyscall(string word, Action<object> handler);

    // a constant: gets a word and a token, resolves to the value
    bool RegisterConstant(string word, long value);
    bool RegisterConstant(string word, float value);

    // closes registration and gives every request its numbers
    IReadOnlyList<Allocation> Allocate(UsedIds used);
}