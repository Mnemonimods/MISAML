namespace MISAML.Core;

public readonly struct HookResult
{
    // tells the prefix whether the original method should be skipped.
    public bool SkipOriginal { get; private init; }

    // value to use if the original method is being replaced.
    public object? Value { get; private init; }

    // creates a result that lets the original method run normally.
    public static HookResult Continue() => new() { SkipOriginal = false };

    // creates a result that skips the original method and uses this value.
    public static HookResult Replace(object? value) => new() { SkipOriginal = true, Value = value };
}