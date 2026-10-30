using System;

namespace MISAML.Godot;

// bound to MISAML.GodotAPI.Primitives once, in Bind(). Reflection happens
// there and nowhere else, after that every call is a normal delegate call.
// The signatures only use types both sides share because of the boundary thing.
internal static class API
{
    public static Func<object, string, object?> Get = null!;
    public static Action<object, string, object?> Set = null!;
    public static Func<object, string, object?[], object?> Call = null!;
    public static Action<object, string, object?[]> CallDeferred = null!;
    public static Func<string, object> Instantiate = null!;
    public static Func<object, bool> IsInstanceValid = null!;
    public static Action<object, string, Action<object?[]>, uint> Connect = null!;
    public static Func<Func<object?[], object?>, object> CreateCallable = null!;
    public static Action<object, string, Action<object?[]>> Disconnect = null!;
    public static Func<string, string, object?[], object?> CallStatic = null!;
    public static Func<string, string, object?> GetStatic = null!;
    public static Action<string, string, object?> SetStatic = null!;
    public static Func<string, object?[], object> Construct = null!;
    public static Func<object, string, object?> GetMember = null!;
    public static Action<object, string, object?> SetMember = null!;
    public static Func<object, string, object?[], object?> CallMember = null!;
    public static Action<Action> RunOnMainThread = null!;
}