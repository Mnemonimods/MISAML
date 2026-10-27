using System;

namespace MISAML.Core.Records;

// what one registry request ended up with. Code and Handler are only for
// syscalls, Value is only for constants (a long or a float)
public record Allocation(string Word, IdKind Kind, int Token, int? Code, Action<object>? Handler, object? Value);