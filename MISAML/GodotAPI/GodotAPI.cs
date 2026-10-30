using System;
using System.Collections.Concurrent;
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
    /// Gets the value of a property of a GodotObject. Nested paths work too, like <c>"position:x"</c>
    /// </summary>
    /// <param name="instance">The GodotObject</param>
    /// <param name="propertyName">The property name to search for</param>
    /// <returns>the value of the property</returns>
    public static object? Get(object instance, string propertyName)
        => FromVariant(((GodotObject)instance).GetIndexed(CachedPath(propertyName)));

    /// <summary>
    /// Sets a property inside a GodotObject with a specific value
    /// </summary>
    /// <param name="instance">The GodotObject</param>
    /// <param name="propertyName">The propertyName to set</param>
    /// <param name="value">The value to set the property to</param>
    public static void Set(object instance, string propertyName, object? value)
        => ((GodotObject)instance).SetIndexed(CachedPath(propertyName), ToVariant(value));

    /// <summary>
    /// Calls a method on a GodotObject with the given args
    /// </summary>
    /// <param name="instance">the GodotObject</param>
    /// <param name="methodName">The method to call</param>
    /// <param name="args">optional args for the method</param>
    /// <returns>the result of the method call</returns>
    public static object? Call(object instance, string methodName, object?[] args)
        => FromVariant(((GodotObject)instance).Call(CachedName(methodName), ToVariants(args)));

    /// <summary>
    /// calls the <paramref name="methodName"/> on the object during idle time <br/>
    /// <b> Note: </b> returns nothing, the called method's result is discarded (use <c>Call</c> if you need it) <br/>
    /// the method to be called has to be in snake_case when referring to godot's built in methods
    /// </summary>
    /// <param name="instance">the GodotObject</param>
    /// <param name="methodName">The method to call</param>
    /// <param name="args">optional args for the method</param>
    public static void CallDeferred(object instance, string methodName, object?[] args)
        => ((GodotObject)instance).CallDeferred(CachedName(methodName), ToVariants(args));

    // returns object not GodotObject, so the bridge doesn't need Godot's types
    /// <summary>
    /// instantiates a godot class
    /// </summary>
    /// <param name="className">the classname to instantiate</param>
    /// <returns>the instantiated class as a GodotObject</returns>
    /// <exception cref="Exception">thrown when classname returns a null/non-object</exception>
    public static object Instantiate(string className)
    {
        var variant = ClassDB.Instantiate(className);
        return variant.AsGodotObject()
            ?? throw new Exception($"ClassDB.Instantiate({className}) returned null/non-object!");
    }

    /// <summary>
    /// Checks if the instance is valid
    /// </summary>
    /// <param name="instance">The instance to check for validity</param>
    /// <returns>
    /// <b>True</b> when the instance is valid <br/>
    /// <b>False</b> when the instance is not valid
    /// </returns>
    public static bool IsInstanceValid(object instance)
        => instance is GodotObject g && GodotObject.IsInstanceValid(g);

    #endregion

    #region signals

    // remembers the Callable made per (object, signal, handler) so Disconnect
    // hands godot that exact one back
    private static readonly Dictionary<(GodotObject, string, Delegate), Callable> Connections = [];

    // handler gets the signals args as an array (empty if none)
    /// <summary>
    /// Connects a signal to a handler
    /// </summary>
    /// <param name="instance">the instance to access</param>
    /// <param name="signalName">the name of the signal to connect</param>
    /// <param name="handler">the handler to call when the signal is emitted</param>
    /// <param name="flags">Connection flags that modify behavior. <b>See:</b> <see href="https://docs.godotengine.org/en/stable/classes/class_object.html#enum-object-connectflags">ConnectFlags</see> in the official Godot docs</param>
    /// <exception cref="ArgumentException">thrown when the specified instance has no signal</exception>
    public static unsafe void Connect(object instance, string signalName, Action<object?[]> handler, uint flags)
    {
        var target = (GodotObject)instance;
        var signal = CachedName(signalName);

        if (!target.HasSignal(signal))
            throw new ArgumentException($"{target.GetType().Name} has no signal '{signalName}'");

        var callable = Callable.CreateWithUnsafeTrampoline(handler, &SignalTrampoline);

        lock (Connections) Connections[(target, signalName, handler)] = callable;
        target.Connect(signal, callable, flags);
    }

    // Godot calls this with as many args the signal has
    /// <summary>
    /// A trampoline function that is called by Godot when a signal is emitted.
    /// </summary>
    /// <param name="delegateObj">Delegate that is inside the callable</param>
    /// <param name="args">arguments of the signal as pointers</param>
    /// <param name="ret">return value back to godot. Is always <c>default</c></param>
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

    /// <summary>
    /// create a Callable that runs a C# function with any number of args and can return values
    /// </summary>
    /// <param name="function">The callable to create</param>
    /// <returns></returns>
    public static unsafe object CreateCallable(Func<object?[], object?> function)
        => Callable.CreateWithUnsafeTrampoline(function, &FunctionTrampoline);

    /// <summary>
    /// like <see cref="SignalTrampoline"/>, but can have a return value
    /// </summary>
    /// <param name="delegateObj">the given delegate, upcast to System.Object</param>
    /// <param name="args">godot_variant arguments</param>
    /// <param name="ret">Return value of type godot_variant</param>
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
    /// Transforms separate native args into .NET Objects
    /// </summary>
    /// <param name="args">The args to convert to an object</param>
    /// <returns>the objects created</returns>
    private static object?[] Unpack(NativeVariantPtrArgs args)
    {
        var values = new object?[args.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = FromVariant(Variant.CreateCopyingBorrowed(args[i]));
        return values;
    }

    /// <summary>
    /// Disconnects a signal from a callable
    /// </summary>
    /// <param name="instance">The instance the callable is in</param>
    /// <param name="signalName">the signal remove</param>
    /// <param name="handler">The handler the signal normally calls</param>
    /// <exception cref="Exception">Thrown when the signal was never connected</exception>
    public static void Disconnect(object instance, string signalName, Action<object?[]> handler)
    {
        var target = (GodotObject)instance;

        Callable callable;
        lock (Connections)
        {
            if (!Connections.Remove((target, signalName, handler), out callable))
                throw new Exception("The signal was never connected");
        }

        target.Disconnect(CachedName(signalName), callable);
    }

    #endregion

    #region static stuff

    /// <summary>
    /// Calls a static function
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="methodName"></param>
    /// <param name="args"></param>
    /// <returns>the result of calling the static member</returns>
    public static object? CallStatic(string typeName, string methodName, object?[] args)
    {
        var result = ResolveType(typeName).InvokeMember(methodName,
            BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding | Stat, null, null, args);
        return result is Variant v ? FromVariant(v) : result;
    }

    /// <summary>
    /// gets a static member
    /// </summary>
    /// <param name="typeName">the static type to get</param>
    /// <param name="memberName">the member to get</param>
    /// <returns>the value of the static member</returns>
    public static object? GetStatic(string typeName, string memberName)
    {
        var result = ResolveType(typeName).InvokeMember(memberName,
            BindingFlags.GetField | BindingFlags.GetProperty | Stat, null, null, null);
        return result is Variant v ? FromVariant(v) : result;
    }

    /// <summary>
    /// sets a static member
    /// </summary>
    /// <param name="typeName">the static type to set</param>
    /// <param name="memberName">the member to set</param>
    /// <param name="value">the value to set the member to</param>
    public static void SetStatic(string typeName, string memberName, object? value)
        => ResolveType(typeName).InvokeMember(memberName,
            BindingFlags.SetField | BindingFlags.SetProperty | Stat, null, null, [value]);

    // builders/constructors
    /// <summary>
    /// Constructs an object from the typeName and provided arguments
    /// </summary>
    /// <param name="typeName">The type to construct</param>
    /// <param name="args">An array of arguments that match in number, order, and type the parameters of the constructor to invoke.
    /// If <c>args</c> is an empty array or null, the constructor that takes no parameters (the parameterless constructor) is invoked.</param>
    /// <returns>The constructed object</returns>
    /// <exception cref="Exception">Thrown when <c>Construct</c> fails to construct the type</exception>
    public static object Construct(string typeName, object?[] args)
        => Activator.CreateInstance(ResolveType(typeName), args)
           ?? throw new Exception($"could not construct {typeName}");

    // non-godot
    /// <summary>
    /// Reads the value of an instance field or property by name, including private
    /// ones. The member is looked up by reflection first-time then cached for
    /// later usage.
    /// </summary>
    /// <param name="instance">the object that contains the member</param>
    /// <param name="name">name of the member invoked</param>
    /// <returns>The current value of the field or property</returns>
    /// <exception cref="MissingMemberException">Thrown when <paramref name="instance"/>
    /// has no field or property with that name.</exception>
    public static object? GetMember(object instance, string name)
        => FindMember(instance.GetType(), name) switch
        {
            FieldInfo f => f.GetValue(instance),
            PropertyInfo p => p.GetValue(instance),
            _ => throw new MissingMemberException(instance.GetType().FullName, name),
        };

    /// <summary>
    /// Sets the value of an instance field or property by name, including
    /// private ones. The member is looked up by reflection first-time then
    /// cached for later usage.
    /// </summary>
    /// <param name="instance">the object that contains the member</param>
    /// <param name="name">name of the member invoked</param>
    /// <param name="value">The value to set</param>
    /// <exception cref="MissingMemberException">Thrown when <paramref name="instance"/> has no field or property with that name</exception>
    public static void SetMember(object instance, string name, object? value)
    {
        switch (FindMember(instance.GetType(), name))
        {
            case FieldInfo f: f.SetValue(instance, value); break;
            case PropertyInfo p: p.SetValue(instance, value); break;
        }
    }

    /// <summary>
    /// Calls a member of an object
    /// </summary>
    /// <param name="instance">The object that contains the member</param>
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

    // string -> StringName/NodePath makes a new finalizable object on every call, so each
    // unique name is converted once and reused
    private static readonly ConcurrentDictionary<string, StringName> StringNames = new();
    private static readonly ConcurrentDictionary<string, NodePath> NodePaths = new();

    private static StringName CachedName(string s) => StringNames.GetOrAdd(s, static n => new StringName(n));
    private static NodePath CachedPath(string s) => NodePaths.GetOrAdd(s, static n => new NodePath(n));

    // field/property per (type, name), found first time, then reused, caching
    private static readonly ConcurrentDictionary<(Type Type, string Name), MemberInfo> MemberCache = new();

    private static MemberInfo FindMember(Type type, string name)
        => MemberCache.GetOrAdd((type, name), static key =>
        {
            var (t, n) = key;
            // field first, then prop, same order as InvokeMember
            return (MemberInfo?)t.GetField(n, Inst)
                ?? t.GetProperty(n, Inst)
                ?? throw new MissingMemberException(t.FullName, n);
        });

    // type name => Type, searched the first time, cached after
    private static readonly ConcurrentDictionary<string, Type> TypeCache = [];

    /// <summary>
    /// Resolves a type by its name in the isolated context. Cached after the first lookup.
    /// </summary>
    /// <param name="typeName">Name of the type to resolve</param>
    /// <returns>Resolved type</returns>
    /// <exception cref="Exception">Thrown when there's no type found in the assembly</exception>
    private static Type ResolveType(string typeName)
        => TypeCache.GetOrAdd(typeName, static name =>
        {
            var isolated = AssemblyLoadContext.GetLoadContext(typeof(Primitives).Assembly)
                ?? throw new Exception("couldn't get the load context of MISAML.GodotAPI");

            foreach (var asmName in new[] { "GodotSharp", "Mnemonimov", "Asm" })
            {
                var asm = isolated.Assemblies.FirstOrDefault(a => a.GetName().Name == asmName);
                var type = asm?.GetType(name);
                if (type != null) return type;
            }

            throw new Exception($"type {name} not found in GodotSharp/Mnemonimov/Asm");
        });

    /// <summary>
    /// Converts a Godot Variant to a C# object
    /// </summary>
    /// <param name="v">the Godot Variant to convert</param>
    /// <returns>A C# object</returns>
    private static object? FromVariant(Variant v) => v.VariantType switch
    {
        Variant.Type.Array => FromGodotArray(v.AsGodotArray()),
        Variant.Type.Dictionary => FromGodotDict(v.AsGodotDictionary()),
        _ => v.Obj
    };

    /// <summary>
    /// Converts a Godot array to a C# <c>object?[]</c>, element by element
    /// </summary>
    /// <param name="array">The Godot array to convert</param>
    /// <returns>A new array with every element converted</returns>
    private static object?[] FromGodotArray(Godot.Collections.Array array)
    {
        var result = new object?[array.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = FromVariant(array[i]);
        return result;
    }

    /// <summary>
    /// Converts a Godot dictionary to a C# <c>Dictionary&lt;object, object?&gt;</c>, key and value
    /// </summary>
    /// <param name="dict">The Godot dictionary to convert</param>
    /// <returns>A new dictionary with every key and value converted</returns>
    private static Dictionary<object, object?> FromGodotDict(Godot.Collections.Dictionary dict)
    {
        var result = new Dictionary<object, object?>(dict.Count);
        foreach (var kv in dict)
            result[FromVariant(kv.Key)!] = FromVariant(kv.Value);
        return result;
    }

    /// <summary>
    /// Converts a C# object to a Godot Variant.
    /// </summary>
    /// <param name="value">The object to convert</param>
    /// <returns>A Godot Variant</returns>
    private static Variant ToVariant(object? value) => value switch
    {
        null => new Variant(),
        Variant v => v,

        // ILSpy GodotSharp.dll then look for Variant.Type for more

        //TODO: literally anything but this, please don't look here :3, bad code doesn't exist!

        // basic types
        bool v => Variant.From(v),
        char v => Variant.From(v),
        sbyte v => Variant.From(v),
        byte v => Variant.From(v),
        short v => Variant.From(v),
        ushort v => Variant.From(v),
        int v => Variant.From(v),
        uint v => Variant.From(v),
        long v => Variant.From(v),
        ulong v => Variant.From(v),
        float v => Variant.From(v),
        double v => Variant.From(v),
        string v => Variant.From(v),

        // objects and collections
        GodotObject x => Variant.From(x),
        Godot.Collections.Array x => Variant.From(x),
        Godot.Collections.Dictionary x => Variant.From(x),

        // maths & engine structs
        Vector2 x => Variant.From(x),
        Vector2I x => Variant.From(x),
        Rect2 x => Variant.From(x),
        Rect2I x => Variant.From(x),
        Transform2D x => Variant.From(x),
        Vector3 x => Variant.From(x),
        Vector3I x => Variant.From(x),
        Vector4 x => Variant.From(x),
        Vector4I x => Variant.From(x),
        Plane x => Variant.From(x),
        Quaternion x => Variant.From(x),
        Aabb x => Variant.From(x),
        Basis x => Variant.From(x),
        Transform3D x => Variant.From(x),
        Projection x => Variant.From(x),
        Color x => Variant.From(x),
        StringName x => Variant.From(x),
        NodePath x => Variant.From(x),
        Rid x => Variant.From(x),
        Callable x => Variant.From(x),
        Signal x => Variant.From(x),

        // packed arrays
        byte[] x => Variant.From(x),
        int[] x => Variant.From(x),
        long[] x => Variant.From(x),
        float[] x => Variant.From(x),
        double[] x => Variant.From(x),
        string[] x => Variant.From(x),
        Vector2[] x => Variant.From(x),
        Vector3[] x => Variant.From(x),
        Vector4[] x => Variant.From(x),
        Color[] x => Variant.From(x),
        StringName[] x => Variant.From(x),
        NodePath[] x => Variant.From(x),
        Rid[] x => Variant.From(x),

        // C# collections get converted element by element
        // (after the packed arrays above, so those keep their fast path)
        System.Collections.IList list => Variant.From(ToGodotArray(list)),
        System.Collections.IDictionary dict => Variant.From(ToGodotDict(dict)),

        // anything left, generic Godot collections, enums. Cached after first use.
        _ => (Variant)VariantFromFor(value.GetType()).Invoke(null, [value])!,
    };

    /// <summary>
    /// Creates a Godot array from any C# list or array, converting each element
    /// </summary>
    /// <param name="list">The C# list to convert</param>
    /// <returns>A Godot array</returns>
    private static Godot.Collections.Array ToGodotArray(System.Collections.IList list)
    {
        var result = new Godot.Collections.Array();
        foreach (var item in list)
            result.Add(ToVariant(item));
        return result;
    }

    /// <summary>
    /// Creates a Godot dictionary from any C# dictionary, converting each key and value
    /// </summary>
    /// <param name="dict">The C# dictionary to convert</param>
    /// <returns>A Godot dictionary</returns>
    private static Godot.Collections.Dictionary ToGodotDict(System.Collections.IDictionary dict)
    {
        var result = new Godot.Collections.Dictionary();
        foreach (System.Collections.DictionaryEntry entry in dict)
            result[ToVariant(entry.Key)] = ToVariant(entry.Value);
        return result;
    }

    /// <summary>
    /// Converts a whole argument array to Variants in one go, for calls
    /// crossing into Godot.
    /// </summary>
    /// <param name="args">The arguments to convert, in order.</param>
    /// <returns>The same arguments as Variants. Empty arrays stay empty.</returns>
    private static Variant[] ToVariants(object?[] args)
    {
        if (args.Length == 0) return [];

        var result = new Variant[args.Length];

        for (var i = 0; i < args.Length; i++)
        {
            result[i] = ToVariant(args[i]);
        }

        return result;
    }

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
        try { File.AppendAllText(LogPath, line + "\n"); }
        catch (Exception) { }
    }
}
