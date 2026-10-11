using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Godot;
using Godot.NativeInterop;

namespace MISAML.GodotAPI;

// primitives surface for API
// NO overloading.
// needs matching thing in GodotBridge
// lazily resolved/built
public static class Primitives
{
    private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Stat = BindingFlags.Public | BindingFlags.Static;

    // Godot objects (property/method system)
    public static object? Get(GodotObject instance, string propertyName)
        => FromVariant(instance.GetIndexed(propertyName));
 
    public static void Set(GodotObject instance, string propertyName, object? value)
        => instance.SetIndexed(propertyName, ToVariant(value));
 
    public static object? Call(GodotObject instance, string methodName, object?[] args)
        => FromVariant(instance.Call(methodName, args.Select(ToVariant).ToArray()));
 
    public static void CallDeferred(GodotObject instance, string methodName, object?[] args)
        => instance.CallDeferred(methodName, args.Select(ToVariant).ToArray());
 
    public static GodotObject Instantiate(string className)
    {
        var variant = ClassDB.Instantiate(className);
        return variant.AsGodotObject()
            ?? throw new Exception($"ClassDB.Instantiate({className}) returned null/non-object!");
    }
 
    public static bool IsInstanceValid(GodotObject instance)
        => GodotObject.IsInstanceValid(instance);

    // signals

    // remembers the Callable made per (object, signal, handler) so Disconnect
    // hands godot that exact one back
    private static readonly Dictionary<(GodotObject, string, Delegate), Callable> _connections = [];

    // handler gets the signals args as an array (empty if none)
    public static unsafe void Connect(GodotObject instance, string signalName, Action<object?[]> handler)
    {
        if (!instance.HasSignal(signalName))
            throw new ArgumentException($"{instance.GetType().Name} has no signal '{signalName}'");

        var callable = Callable.CreateWithUnsafeTrampoline(handler, &SignalTrampoline);

        lock (_connections) _connections[(instance, signalName, handler)] = callable;
        instance.Connect(signalName, callable);
    }

    // Godot calls this with as many args the signal has
    private static void SignalTrampoline(object delegateObj, NativeVariantPtrArgs args, out godot_variant ret)
    {
        var values = new object?[args.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = FromVariant(Variant.CreateCopyingBorrowed(args[i]));

        try { ((Action<object?[]>)delegateObj)(values); }
        catch (Exception e) { Logger.Write(nameof(Primitives), $"signal handler threw: {e}"); }

        ret = default;
    }

    public static void Disconnect(GodotObject instance, string signalName, Action<object?[]> handler)
    {
        Callable callable;
        lock (_connections)
        {
            if (!_connections.Remove((instance, signalName, handler), out callable)) return; // never connected
        }
        instance.Disconnect(signalName, callable);
    }

    // static stuff

    public static object? CallStatic(string typeName, string methodName, object?[] args)
    {
        var result = ResolveType(typeName).InvokeMember(methodName,
            BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding | Stat, null, null, args);
        return result is Variant v ? FromVariant(v) : result;
    }
 
    public static object? GetStatic(string typeName, string memberName)
    {
        var result = ResolveType(typeName).InvokeMember(memberName,
            BindingFlags.GetField | BindingFlags.GetProperty | Stat, null, null, null);
        return result is Variant v ? FromVariant(v) : result;
    }
 
    public static void SetStatic(string typeName, string memberName, object? value)
        => ResolveType(typeName).InvokeMember(memberName,
            BindingFlags.SetField | BindingFlags.SetProperty | Stat, null, null, [value]);

    // builders/constructors

    public static object Construct(string typeName, object?[] args)
        => Activator.CreateInstance(ResolveType(typeName), args)
            ?? throw new Exception($"could not construct {typeName}");

    // non-godot

    public static object? GetMember(object instance, string name)
        => instance.GetType().InvokeMember(name,
            BindingFlags.GetField | BindingFlags.GetProperty | Inst, null, instance, null);
 
    public static void SetMember(object instance, string name, object? value)
        => instance.GetType().InvokeMember(name,
            BindingFlags.SetField | BindingFlags.SetProperty | Inst, null, instance, [value]);
 
    public static object? CallMember(object instance, string name, object?[] args)
        => instance.GetType().InvokeMember(name,
            BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding | Inst, null, instance, args);

    // threading

    // runs on Godot main thread, next idle moment, safe to call from any thread
    public static void RunOnMainThread(Action action)
        => Callable.From(action).CallDeferred();

    // internals

    // searches only the isolated context
    private static Type ResolveType(string typeName)
    {
        var isolated = AssemblyLoadContext.GetLoadContext(typeof(Primitives).Assembly)!;
 
        foreach (var asmName in new[] { "GodotSharp", "Mnemonimov", "Asm" })
        {
            var asm = isolated.Assemblies.FirstOrDefault(a => a.GetName().Name == asmName);
            var type = asm?.GetType(typeName);
            if (type != null) return type;
        }
 
        throw new Exception($"type {typeName} not found in GodotSharp/Mnemonimov/Asm");
    }

    // Godot => C#
    private static object? FromVariant(Variant v) => v.VariantType switch
    {
        Variant.Type.Array => v.AsGodotArray().Select(x => FromVariant(x)).ToArray(),
        Variant.Type.Dictionary => v.AsGodotDictionary().ToDictionary(kv => FromVariant(kv.Key)!, kv => FromVariant(kv.Value)),
        _ => v.Obj,
    };

    // C# => Godot
    private static Variant ToVariant(object? value) => value switch
    {
        null => new Variant(),
        Variant v => v,
        object?[] arr => Variant.From(new Godot.Collections.Array(arr.Select(x => ToVariant(x)))),
        Dictionary<object, object?> dict => Variant.From(ToGodotDict(dict)),
        _ => (Variant)VariantFromFor(value.GetType()).Invoke(null, [value])!,
    };

    // Variant.From<T> generic, so each C# type needs own closed ver,
    // built the first time that type is used, then cached
    private static readonly Dictionary<Type, MethodInfo> _fromCache = [];

    private static MethodInfo VariantFromFor(Type type)
    {
        lock (_fromCache)
        {
            if (!_fromCache.TryGetValue(type, out var method))
            {
                method = typeof(Variant).GetMethods()
                    .First(m => m.Name == "From" && m.IsGenericMethodDefinition
                             && m.GetParameters().Length == 1
                             && m.GetParameters()[0].ParameterType.IsByRef)
                    .MakeGenericMethod(type);
                _fromCache[type] = method;
            }
            return method;
        }
    }

    private static Godot.Collections.Dictionary ToGodotDict(Dictionary<object, object?> dict)
    {
        var result = new Godot.Collections.Dictionary();
        foreach (var kv in dict)
            result[ToVariant(kv.Key)] = ToVariant(kv.Value);
        return result;
    }
}

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(Logger).Assembly.Location)!,
        "misaml.log"
    );
 
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public static void Write(string @class, string message)
    {
        var line = $"{_stopwatch.Elapsed.TotalSeconds:F6} [MISAML.GodotAPI.{@class}] {message}";
        File.AppendAllText(LogPath, line + "\n");
    }
}
