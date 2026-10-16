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

/// <summary>
/// Primitives surface for the API
/// </summary>
public static class Primitives
{
    private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    #region Godot objects (property/method system)
    
    /// <summary>
    /// Gets the property name of a godotObject
    /// </summary>
    /// <param name="instance">The GodotObject</param>
    /// <param name="propertyName">The property name to search for</param>
    /// <returns>the property name to be searched</returns>
    public static object? Get(GodotObject instance, string propertyName)
        => FromVariant(instance.GetIndexed(propertyName));

    /// <summary>
    /// Sets a property inside a GodotObject with a specific value
    /// </summary>
    /// <param name="instance">The GodotObject</param>
    /// <param name="propertyName">The propertyName to set</param>
    /// <param name="value">The value to set the property to</param>
    public static void Set(GodotObject instance, string propertyName, object? value)
        => instance.SetIndexed(propertyName, ToVariant(value));

    /// <summary>
    /// Calls a property inside a GodotObject with specified args <br/>
    /// <b> Note: </b> the method to be called has to be in snake_case
    /// </summary>
    /// <param name="instance">the GodotObject</param>
    /// <param name="methodName">The method to call</param>
    /// <param name="args">optional args for the method</param>
    /// <returns>the result of the method call</returns>
    public static object? Call(GodotObject instance, string methodName, object?[] args)
        => FromVariant(instance.Call(methodName, args.Select(ToVariant).ToArray()));

    /// <summary>
    ///
    /// <b> Note: </b> this always will return NULL, not the result of the method called
    /// </summary>
    /// <param name="instance"></param>
    /// <param name="methodName"></param>
    /// <param name="args"></param>
    public static void CallDeferred(GodotObject instance, string methodName, object?[] args)
        => instance.CallDeferred(methodName, args.Select(ToVariant).ToArray());
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="className"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// 
    public static GodotObject Instantiate(string className)
    {
        var variant = ClassDB.Instantiate(className);
        return variant.AsGodotObject()
               ?? throw new Exception($"ClassDB.Instantiate({className}) returned null/non-object!");
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    public static bool IsInstanceValid(GodotObject instance)
        => GodotObject.IsInstanceValid(instance);

    #endregion

    #region signals

    // remembers the Callable made per (object, signal, handler) so Disconnect
    // hands godot that exact one back
    private static readonly Dictionary<(GodotObject, string, Delegate), Callable> _connections = [];

    // handler gets the signals args as an array (empty if none)
    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <param name="signalName"></param>
    /// <param name="handler"></param>
    /// <exception cref="ArgumentException"></exception>
    
    public static unsafe void Connect(GodotObject instance, string signalName, Action<object?[]> handler)
    {
        if (!instance.HasSignal(signalName))
            throw new ArgumentException($"{instance.GetType().Name} has no signal '{signalName}'");

        var callable = Callable.CreateWithUnsafeTrampoline(handler, &SignalTrampoline);

        lock (_connections) _connections[(instance, signalName, handler)] = callable;
        instance.Connect(signalName, callable);
    }

    // Godot calls this with as many args the signal has
    /// <summary>
    /// 
    /// </summary>
    /// <param name="delegateObj"></param>
    /// <param name="args"></param>
    /// <param name="ret"></param>
    private static void SignalTrampoline(object delegateObj, NativeVariantPtrArgs args, out godot_variant ret)
    {
        var values = Unpack(args);

        try
        {
            ((Action<object?[]>)delegateObj)(values);
        }
        catch (Exception e)
        {
            Logger.Write(nameof(Primitives), $"signal handler threw: {e}");
        }

        ret = default;
    }

    // create a Callable that runs a C# function, any number of args, can return a value
    /// <summary>
    /// 
    /// </summary>
    /// <param name="function"></param>
    /// <returns></returns>
    public static unsafe object CreateCallable(Func<object?[], object?> function)
        => Callable.CreateWithUnsafeTrampoline(function, &FunctionTrampoline);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="delegateObj"></param>
    /// <param name="args"></param>
    /// <param name="ret"></param>
    private static void FunctionTrampoline(object delegateObj, NativeVariantPtrArgs args, out godot_variant ret)
    {
        try
        {
            var result = ((Func<object?[], object?>)delegateObj)(Unpack(args));
            ret = ToVariant(result).CopyNativeVariant();
        }
        catch (Exception e)
        {
            Logger.Write(nameof(Primitives), $"callable threw: {e}");
            ret = default;
        }
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private static object?[] Unpack(NativeVariantPtrArgs args)
    {
        var values = new object?[args.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = FromVariant(Variant.CreateCopyingBorrowed(args[i]));
        return values;
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <param name="signalName"></param>
    /// <param name="handler"></param>
    public static void Disconnect(GodotObject instance, string signalName, Action<object?[]> handler)
    {
        Callable callable;
        lock (_connections)
        {
            if (!_connections.Remove((instance, signalName, handler), out callable)) return; // never connected
        }

        instance.Disconnect(signalName, callable);
    }

    #endregion

    #region static stuff
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="methodName"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    public static object? CallStatic(string typeName, string methodName, object?[] args)
    {
        var result = ResolveType(typeName).InvokeMember(methodName,
            BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding | Stat, null, null, args);
        return result is Variant v ? FromVariant(v) : result;
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="memberName"></param>
    /// <returns></returns>
    public static object? GetStatic(string typeName, string memberName)
    {
        var result = ResolveType(typeName).InvokeMember(memberName,
            BindingFlags.GetField | BindingFlags.GetProperty | Stat, null, null, null);
        return result is Variant v ? FromVariant(v) : result;
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="memberName"></param>
    /// <param name="value"></param>
    public static void SetStatic(string typeName, string memberName, object? value)
        => ResolveType(typeName).InvokeMember(memberName,
            BindingFlags.SetField | BindingFlags.SetProperty | Stat, null, null, [value]);

    // builders/constructors
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static object Construct(string typeName, object?[] args)
        => Activator.CreateInstance(ResolveType(typeName), args)
           ?? throw new Exception($"could not construct {typeName}");

    // non-godot
    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <param name="name"></param>
    /// <returns>An object representing the return value of the invoked member.</returns>
    public static object? GetMember(object instance, string name)
        => instance.GetType().InvokeMember(name,
            BindingFlags.GetField | BindingFlags.GetProperty | Inst, null, instance, null);
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <param name="name"></param>
    /// <param name="value"></param>
    public static void SetMember(object instance, string name, object? value)
        => instance.GetType().InvokeMember(name,
            BindingFlags.SetField | BindingFlags.SetProperty | Inst, null, instance, [value]);

    /// <summary>
    /// Calls a member of an object
    /// </summary>
    /// <param name="instance">The instance to call</param>
    /// <param name="name">The name of the member to invoke</param>
    /// <param name="args">Args to pass along</param>
    /// <returns>An object representing the return value of the invoked member.</returns>
    public static object? CallMember(object instance, string name, object?[] args)
        => instance.GetType().InvokeMember(name,
            BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding | Inst, null, instance, args);

    #endregion

    #region threading
    
    /// <summary>
    /// Runs the specified action on the Godot main thread during the next idle moment. Safe to call from any thread
    /// </summary>
    /// <param name="action">The action to run</param>
    public static void RunOnMainThread(Action action)
        => Callable.From(action).CallDeferred();

    #endregion

    #region internals

    /// <summary>
    /// Resolves a type by its name in the isolated context.
    /// </summary>
    /// <param name="typeName">Name of the type to resolve</param>
    /// <returns>Resolved type</returns>
    /// <exception cref="Exception">Thrown when there's no type found in the assembly</exception>
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

    /// <summary>
    /// Converts a Godot Variant to a C# object
    /// </summary>
    /// <param name="v">the Godot Variant to convert</param>
    /// <returns>A C# object</returns>
    private static object? FromVariant(Variant v) => v.VariantType switch
    {
        Variant.Type.Array => v.AsGodotArray().Select(FromVariant).ToArray(),
        Variant.Type.Dictionary => v.AsGodotDictionary()
            .ToDictionary(kv => FromVariant(kv.Key)!, kv => FromVariant(kv.Value)),
        _ => v.Obj
    };

    /// <summary>
    /// Converts a C# object to a Godot Variant.
    /// </summary>
    /// <param name="value">The object to convert</param>
    /// <returns>A Godot Variant</returns>
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
    private static readonly Dictionary<Type, MethodInfo> FromCache = [];

    /// <summary>
    /// Returns a Variant.From for a given type
    /// </summary>
    /// <param name="type">The type to convert to a Variant</param>
    /// <returns>A MethodInfo for the Variant.From method</returns>
    private static MethodInfo VariantFromFor(Type type)
    {
        lock (FromCache)
        {
            if (FromCache.TryGetValue(type, out var method)) return method;

            method = typeof(Variant).GetMethods()
                .First(m => m is { Name: "From", IsGenericMethodDefinition: true }
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType.IsByRef)
                .MakeGenericMethod(type);
            FromCache[type] = method;

            return method;
        }
    }

    /// <summary>
    /// Creates a new GodotDictionary
    /// </summary>
    /// <param name="dict">
    /// The C# dictionary to convert
    /// </param>
    /// <returns>A GodotDictionary</returns>
    private static Godot.Collections.Dictionary ToGodotDict(Dictionary<object, object?> dict)
    {
        var result = new Godot.Collections.Dictionary();
        foreach (var kv in dict)
            result[ToVariant(kv.Key)] = ToVariant(kv.Value);
        return result;
    }
    #endregion
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
