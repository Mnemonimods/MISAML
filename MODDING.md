# Writing a MISAML mod

This is a guide on writing a mod for [Mnemonimov](https://store.steampowered.com/app/3854110/Mnemonimov/) with MISAML.

Check the example mods under the organisation page on GitHub for further examples.

## Quick start

If you just want a mod running, follow these steps in order:

1. Make a folder for your mod, with a C# class library project in it that targets .NET 8 (see [Building a C# mod](#building-a-c-mod)).
2. Reference `MISAML.dll` in the project file.
3. Write a `public static` method with no parameters. MISAML calls it once when it loads your mod. Register your hooks, syscalls and constants inside it (see [Writing a hook](#writing-a-hook) and [Syscalls and constants](#syscalls-and-constants)).
4. Write a `mod.json` that points at your DLL and that method (see [Mod folders and mod.json](#mod-folders-and-modjson)).
5. Build, then copy the DLL and `mod.json` into a new folder under `mods/`.
6. Start the game with `misaml-cli`, then open `misaml.log` and look for the line `<your modId>: loaded OK`.

A complete worked example is in [Putting it all together](#putting-it-all-together).

## How MISAML loads a mod

It helps to know the order MISAML loads a mod, as most "Why doesn't my mod work" problems come from doing something too early or too late.

1. **MISAML starts with the game.** This is before the game's scene tree exists. Nothing from Godot is ready yet.
2. **MISAML lists the folders in `mods/`.** They are sorted alphabetically. A folder whose name starts with a dot is skipped.
3. **Each `mod.json` is read, parsed and checked.** A folder with no or an invalid `mod.json` is skipped and the reason is logged. If two mods have the same `modId`, the first folder wins and the other is skipped.
4. **`misaml.toml` is applied.** Mods the user turned off are dropped, version pins are checked, priority is recorded. If `misaml.toml` exists but can't be read, no mods load.
5. **MISAML decides the load order.** A mod always loads after the mods it depends on. Beyond that, a lower priority number loads first, and ties go by `modId`. A mod whose dependency is missing, turned off or broken is skipped.
6. **Each mod's entry method runs, one mod at a time.** Your DLL is loaded into the same context as `MISAML.dll`. Everything you register while the entry method runs (hooks, syscalls, constants) is recorded as belonging to your mod. This is where you register anything.
7. **Entry scripts are queued.** A GDScript mod waits until the game is ready.
8. **The game starts.** The first time the game calls into MISAML, two things happen. Firstly, every syscall and constant that mods registered gets its number and is written into the game. Then the "ready" callbacks run on the main thread, `bridge.OnReady(...)` code, GDScript mods (which are created and added to the scene tree), etc.

What this means:

- Register hooks, syscalls and constants **inside the entry method** and not later. Things registered later aren't recorded as yours (see [Writing a hook](#writing-a-hook)).
- Don't touch Godot until `OnReady`.
- A mod that throws while loading is skipped, and the mods that depend on it are skipped too. Other mods carry on.

## Mod folders and mod.json

A mod is a folder in `mods/` (next to the game executable). It has a `mod.json` and either a C# DLL, a GDScript, or both.

```
mods/
  examplemod/
    mod.json
    ExampleMod.dll
    main.gd
```

The folder name doesn't matter. The mod's identity is the `modId` inside `mod.json`.

```json
{
    "modId": "ExampleMod",
    "modName": "Example Mod",
    "author": "JJRol",
    "description": "This is an example description.",
    "version": "0.1.0",
    "source": "https://github.com/user/example-mod",
    "sourceManifest": "https://raw.githubusercontent.com/user/example-mod/main/mod.json",
    "misamlVersion": "^0.1.0",
    "entryAssembly": "ExampleMod.dll",
    "entryMethod": "ExampleMod.Main.Start",
    "entryScript": "main.gd",
    "dependencies": {
        "SomeMod": "^1.0.0"
    }
}
```

Field names are not case sensitive.

| Field | Required | Notes |
|---|---|---|
| `modId` | yes | Letters, digits, `.`, `_`, `-`. It must start with a letter or digit. Max 64 characters. Not case sensitive. If two mods have the same id, the first folder (in alphabetical order) wins and the other is skipped. |
| `modName` | no | Shown to the user. Defaults to `modId`. |
| `version` | no | A version like `1.2.3`. Defaults to `0.0.0`. Keep it the same as your release tag. |
| `author`, `description` | no | |
| `source` | no | Where the mod lives. |
| `sourceManifest` | no | A link to the raw `mod.json`. Not used by 0.1.0. |
| `misamlVersion` | no | The MISAML versions the mod was made for, as a version range. If it doesn't match, you get a warning in the log. The mod still loads. |
| `entryAssembly` | one of these | A DLL in the mod folder. |
| `entryMethod` | with `entryAssembly` | `Namespace.Class.Method`, or `Class.Method`. MISAML splits it at the last dot. The method must be `public static` with no parameters. |
| `entryScript` | one of these | A GDScript file in the mod folder. |
| `dependencies` | no | See below. |

A mod needs `entryAssembly`, `entryScript`, or both. With neither, a mod will fail to load.

### Version ranges

Versions are semver (`X.Y.Z`, e.g. `1.2.3`). Ranges such as `^1.1.0` are used in `misamlVersion`, in `dependencies` and in the version pin in `misaml.toml`. If a range can't be understood, the log will say so.

### Dependencies

Each dependency has a mod id and a `version`, which is a semver range such as `^1.0.0`. You can write it as just the version, or as an object if you also want to give a `source`:

```json
"dependencies": {
    "SomeMod": "^1.0.0",
    "OtherMod": { "version": "^2.1.0", "source": "https://github.com/user/other-mod" }
}
```

`source` has to be a link formatted like `https://github.com/owner/repo`. Other keys inside a dependency are ignored with a warning in the log.

**As of MISAML v0.1.0, MISAML does not download dependencies.** The user has to install them. MISAML only checks that each dependency is installed, enabled, loaded, and the right version, and loads them before the mods that need them. If a dependency isn't, the mod that needs it is skipped and the reason is written to `misaml.log`. When a dependency has a `source`, the log message says "Get it from" and gives that link, so put it on every dependency your users might not have. Mention your dependencies in your mod's README too.

A dependency can't be listed twice in the same `mod.json`. If mods depend on each other in a circle, they are all skipped, and the log shows the loop (for example `dependency cycle: A -> B -> A`).

## Finding methods to hook

MISAML doesn't ship with a list of the game's methods, just due to the gargantuan size the list would need to be. This is how you find them yourself.

1. Know where the game's code is

The game is a Godot project with its C# code in a few DLLs, in the data folder next to the game executable:
- `Mnemonimov.dll`: The game's own scripts, including the bridge classes the UI talks to.
- `Asm.dll`: The whole MISA assembly toolchain. The lexer, parser, code generator, labels and symbols, diagnostics, and the virtual machine that runs the result.
- `GodotSharp.dll`: Godot's own C# API.

2. Decompile them

Install [ILSpy's command-line tool](https://github.com/icsharpcode/ILSpy) and decompile each DLL into a folder of `.cs` files:

```sh
dotnet tool install -g ilspycmd
ilspycmd Mnemonimov.dll -o mnemonimov_output -p
ilspycmd Asm.dll -o asm_output -p
```

Now you can read and search the game's code like any project. Open the folders in your IDE of choice so you can jump to definitions.

The `-p` option makes a project with one folder per namespace, so **the folder name is the namespace**. `Asm.Running/VirtualMachine.cs` is the type `Asm.Running.VirtualMachine`. This is the name you give `hooks.Register`.

At the time of writing, the folders look like this:

`Asm.dll`

| Folder | What's in it |
|---|---|
| `Asm.Running` | `VirtualMachine` (runs programs, handles syscalls) and `Assembler` (runs the whole assemble step from source to program). Most mods start here. |
| `Asm.Frontend` | Reading the source: `Lexer`, `Parser`, `Token`, and the syntax tree nodes (`Instruction`, `SystemCall`, `Directive`, `Label`, `Expression`, and so on). |
| `Asm.Backend` | Turning the syntax tree into a program: `CodeGenerator`, `LabelMapper`, `SymbolTable`, `ExpressionEvaluator`, file loading and includes. |
| `Asm.General` | Shared definitions: `OpCode`, `RegCode`, `ScCode` (syscall codes), `DtCode`, `BuiltInProcess`, `EnumMaps`, `SymbolFinder`, `DiagnosticSink`. |
| `Asm.Common` | Byte reading and writing helpers, and `ProgramProcessor`. |

`Mnemonimov.dll`

| Folder | What's in it |
|---|---|
| `Mnemonimov.src.asm` | The bridge classes the UI calls: `VirtualMachineRunner`, `AssemblerRunner`, `ShellLexer`, `TextSearcher`, `CsUtils`. |
| `GodotPlugins.Game` | `Main.cs`, the plugin entry point. |

Ignore the `System.Text.RegularExpressions.Generated` folder and `--z__ReadOnlyArray.cs`. The compiler generated them and they aren't game logic.

Your folders may differ after a game update.

If you want to add a syscall or constant, look at `OpCode`, `ScCode` and `EnumMaps` in `Asm.General` to see how a game stores these numbers. MISAML's ID registry (`ids.RegisterSyscall`) does the work of picking free numbers for you, so you usually don't need to touch them.

3. Search for what you want

Some searches that work:

```sh
# the classes the game exposes to its UI (a good place to start)
grep -rl "GlobalClass" decompiled_mnemonimov/

# a specific method, by name
grep -rn "HandleSystemCall" decompiled_asm/

# every place a word or message appears (good for tracing a feature from what you see in the game)
grep -rn "unterminated" decompiled_mnemonimov/

# who calls a method
grep -rn "SetStep(" decompiled_mnemonimov/ decompiled_asm/
```

Searching for a message that you can see in the game (an error, a label) is often the fastest way in. Find the string, then read the method around it.

Write down three things for the method you want:

- the full type name, namespace included (e.g. `Asm.Running.VirtualMachine`),
- the method name,
- the parameter types, if the method is overloaded.

That is all `hooks.Register("Asm.Running.VirtualMachine", "HandleSystemCall", handler)` needs.

4. Pick the right place to hook

- **Bridge classes** in `Mnemonimov.src.asm` (`VirtualMachineRunner`, `AssemblerRunner`, `ShellLexer`, `TextSearcher`, `CsUtils`) are what the game's UI calls. Many of their methods just forward to a method in `Asm.dll`, but some reshape the result or do their own work, so read the method before you decide. These are a good place to start because their names are mostly stable, but they're hooked the same way as everything else.
- **If several bridge methods end in the same real method, hook the real one.** For example, there are three stepping methods (`StepOver`, `StepInto`, `StepOut`), all call one `SetStep`, so one hook here covers all three.
- **Private and internal methods** can be hooked as well. These are more likely to be renamed or removed in a game update, so expect to recheck these.
- **Don't hook into Godot's generated interop methods** (names like `InvokeGodotClassMethod`, `HasGodotClassMethod`, `SaveGodotObjectData`). They take types Harmony can't rewrite and throw `InvalidProgramException`.

5. Check the method can be hooked

Start the game with `MISAML_DEBUG=1`. MISAML then tries each and every candidate method and writes the results to `misaml.log`. A line starting with `OK|` means that the method can be patched. Search the log for your method name:

```sh
grep 'HandleSystemCall' misaml.log
```

Methods on the bridge classes show `already patched elsewhere`. That is fine, because MISAML has already patched them. The sweep produces a very large log and slows startup, and `misaml.log` is cleared each launch, so copy the lines you need before you start the game again. Don’t leave debug mode on.

## Writing a hook

A hook runs your code when a game method is called.

```csharp
var hooks = new Hooks();

hooks.Register("Asm.Running.VirtualMachine", "HandleSystemCall", (vm, args) =>
{
    Console.WriteLine($"syscall args: {string.Join(", ", args)}");
    return HookResult.Continue();
});
```

A handler gets the instance the method was called on (`null` for a static method) and the arguments. It returns one of:

- `HookResult.Continue()` to let the game's method run as normal. A hook that only logs should always return this.
- `HookResult.Replace(value)` to skip the game's method and return `value` instead (the `value` is ignored for void methods).

If you have the type, you can use `hooks.Register<T>("MethodName", handler)` instead of the type name.

A few things to know:
- The method is patched the first time you register a hook for it. A hook on a method that MISAML already patches works the same way.
- If a method has several overloads, you have to say which one you want (or all of them). See "Choosing an overload" below.
- Any number of mods can hook the same method. Handlers run in priority order, lowest number first, and equal priorities run in the order they were registered. A mod's priority comes from `misaml.toml` if the user sets one. The default is 100.
- The first `Replace` wins. The rest of the chain doesn't run, so a handler later in the chain may never see the call.
- A handler that throws is logged and skipped, and the game carries on as if it had returned `Continue()`.
- Every hook adds a little work to every call of that method. Patch the method you need and nothing broader, and keep the handler cheap. `HandleSystemCall`, for example, runs very often.

### Choosing an overload

Some methods may have several versions with the same name but different parameters (C# overloads). The method name you pass to `Register` decides which one you will hook:

| You write | It hooks |
|---|---|
| `"Foo"` | The method, **if it has only one version**. |
| `"Foo(System.String,System.Int32)"` | Only the version with those parameter types. |
| `"Foo(string,int)"` | The same. C# keywords and .NET names for the basic types are accepted (`bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `char`, `string`, `object`, `nint`, `nuint`). Spaces are ignored. |
| `"Foo()"` | Only the version with no parameters. |
| `"Foo(*)"` | Every version. |

If you write a bare `"Foo"` and the method has several versions, MISAML logs an error that lists them and **does not hook anything**. Copy one of the signatures from the error message, or use `"Foo(*)"` if you want to hook all of them.

```csharp
// one specific overload
hooks.Register("Some.Game.Type", "DoThing(string,int)", (self, args) => HookResult.Continue());

// every overload
hooks.Register("Some.Game.Type", "DoThing(*)", (self, args) => HookResult.Continue());
```

For types that aren't basic types, write the full name, for example `Godot.Collections.Dictionary`.

### What a hook can and can't target

- It can target instance and static methods, public or not.
- It finds a method on a type or on a type it inherits from. For a static method, register on the type that declares it. MISAML logs an error that says so if you don't.
- It can't target abstract methods, generic methods, or methods declared on `System.Object`. This will be logged, and the logs will say which.

### Things to watch for

- **Register inside your entry method.** MISAML records which mod a hook belongs to while the entry method runs. A hook registered later, such as inside an `OnReady` callback, isn't recorded as yours. The log then shows your assembly name instead of your mod id, and a priority set for you in `misaml.toml` isn't applied.
- **"Registered" doesn't mean "patched".** The log line `successfully registered handler from <mod> (priority N), position X of Y` means MISAML accepted your handler. When it also finds and patches the game's method, you get a second line: `patched <type>::<method>(<signature>)`. If you never see that line, the method wasn't found. Either the type or method name is wrong, or the game's assembly hasn't loaded yet (MISAML waits and patches as soon as it does). A wrong name is not reported as an error. If another hook already patched the same method, you won't get a second `patched` line, but your handler is still in the chain.
- **A replacement has to fit.** For a method that returns a value, the value in `HookResult.Replace(value)` has to be of the method's return type. If it isn't, MISAML logs a warning and runs the game's method instead. `null` can't replace a value type such as `int`.
- **Priority.** The default is 100. The priority you pass in code is only a default for your mod. If the user sets a priority for your mod in `misaml.toml`, it replaces the number you pass, for every hook your mod registers.

## Syscalls and Constants

If what you want is a new syscall or constant, you don't need to write a hook yourself:

```csharp
var ids = new Ids();

ids.RegisterSyscall("SYS_HELLO", vm => { /* ... */ });
ids.RegisterConstant("LIMIT", 100L);
ids.RegisterConstant("SCALE", 1.5f);
```

You don't pick numbers. MISAML gives each word a free number once every mod has been loaded, then writes the words into the game's lexer and assembler tables and hooks `HandleSystemCall` once for all of them. The words work in the editor and the assembler. Each call returns `false` if it was refused (for example, if the word was already in use).

A syscall handler gets the game's VM object. To read registers and memory from this object, see the next section.

## Talking to Godot and the game: the bridge

Your mod can't use Godot's types directly. They live in the game's own isolated `AssemblyLoadContext`, and a second copy in your DLL would be a different set of types, hence causing conflicts. So, a mod holds a Godot object as a plain `object` and asks `GodotBridge` to do things with it:

```csharp
var bridge = new GodotBridge();
```

You only ever use `GodotBridge`. Behind `GodotBridge` is a small layer called `Primitives` that does the actual work inside the game's context. Most developers never need to call this themselves, but it explains the rules below if you want to take a look.

Every call takes the object and a name as strings. If a name is wrong, you find out when the call runs, not when you build. This is unavoidable.

### Godot Objects

Use these for nodes, textures, and anything else that is a Godot object.

| Call | Does |
|---|---|
| `bridge.Get(obj, "name")` | Reads a property. Nested paths work: `"position:x"`. |
| `bridge.Set(obj, "name", value)` | Sets a property. |
| `bridge.Call(obj, "method", args...)` | Calls a method and returns the result. |
| `bridge.CallDeferred(obj, "method", args...)` | Calls a method at the next idle moment. The result is thrown away. |
| `bridge.Instantiate("ClassName")` | Makes a new Godot object, such as `"Label"`. |
| `bridge.IsInstanceValid(obj)` | `false` once the object has been freed. Check this before using an object you've kept hold of. |

Godot's own methods and properties use `snake_case` names (`add_child`, `set_shader_parameter`), the same as in GDScript.

### Getting at the scene

To reach the scene tree, use a static call:

```csharp
var label = bridge.Instantiate("Label");
bridge.Set(label, "text", "hello");

var tree = bridge.CallStatic("Godot.Engine", "GetMainLoop")!;
var root = bridge.Get(tree, "root")!;
bridge.Call(root, "add_child", label);
```

`CallStatic`, `GetStatic` and `SetStatic` take the full type name and the member name. MISAML looks up the type in the `GodotSharp` assembly, then the game's `Mnemonimov`, then `Asm`, so a full name from the decompiled code works (`Godot.Engine`, `Asm.Running.VirtualMachine`). `bridge.Construct("TypeName", args...)` makes a new instance of any of those types.

Mods load before the scene tree exists, so put all of this inside `bridge.OnReady(() => { ... })`. This runs on the main thread once the game has started and is ready.

### Game objects that aren't Godot objects

The VM, the assembler and the rest of the code are ordinary .NET objects. For these, use the member calls. They work on any object and can reach **private fields and properties**:

| Call | Does |
|---|---|
| `bridge.GetMember(obj, "name")` | Reads a field or property. |
| `bridge.SetMember(obj, "name", value)` | Writes one. |
| `bridge.CallMember(obj, "method", args...)` | Calls a method. |

This is how a syscall handler gets at the VM's registers and memory. The handler gets the VM object, and reads what you need from it:

```csharp
ids.RegisterSyscall("SYS_EXAMPLE", vm => {
    var registers = (int[])bridge.GetMember(vm, "_registers")!;
    // ...
});
```

A member is looked up the first time and cached, so repeat reads are relatively cheap. If you read the same member on every call anyway, keep the result in a variable and reuse it. Look at the decompiled `VirtualMachine` for the real names and types. They are the game's internals and can change in any update.

### Values

Numbers, strings, booleans and Godot's own types (vectors, colours, rects and so on) go across as they are. Collections are converted:

- **Going in** (arguments and `Set`): C# arrays and lists become Godot arrays, and C# dictionaries become Godot dictionaries, converted item by item. `byte[]`, `int[]`, `string[]` and the other primitive arrays become Godot's packed arrays.
- **Coming out** (results): a Godot array becomes an `object?[]` and a Godot dictionary becomes a `Dictionary<object, object?>`. Every item comes out as `object`, so check the type before you cast.
- A Godot object comes out as `object`. Hand it back to `Get`, `Set` and `Call` to use it.

**Watch out for passing an array as an argument.** `Call`, `CallStatic`, `Construct` and `CallMember` take `params object?[] args`. If you pass a single array of a reference type, such as `string[]`, C# treats it as the whole argument list and spreads it, so the method gets the strings as separate arguments. Arrays of value types (`byte[]`, `int[]`) don't do this. To pass the array itself as one argument, wrap it:

```csharp
bridge.Call(node, "some_method", (object)Strings);
// or
bridge.Call(node, "some_method", new object[] { Strings });
```

### Signals and Callbacks

```csharp
Action<object?[]> onPressed = args => { /* ... */ };

bridge.Connect(button, "pressed", onPressed);
bridge.Disconnect(button, "pressed", onPressed);
```

The handler gets the signals' arguments as an array (empty if there are none). To disconnect, pass the same handler you connected, not an identical-looking lambda, so keep it in a variable. Disconnecting something that was never connected throws, and so does connecting to a signal the object doesn't have.

If your handler throws, the exception is written to `misaml.log` and the game carries on.

`bridge.CreateCallable(args => result)` makes a Godot `Callable` out of a C# function, for APIs that want one.

### Threads

Godot objects have to be used from the main thread. The VM runs on its own thread, so a syscall handler is not on the main thread. If a handler needs to touch Godot, hand the work over:

```csharp
bridge.RunOnMainThread(() => bridge.Call(node, "queue_redraw"));
```

`OnReady` callbacks already run on the main thread. Keep the work inside the handler cheap (set a flag, store a value) and let the main thread act on it.

### Errors

When a call fails, you get the real exception, not a wrapper around it. A wrong type name or member name throws (`MissingMemberException`, or "type not found in GodotSharp/Mnemonimov/Asm"). Look in the `misaml.log` file first.

## Putting it all together

A complete mod that adds one syscall, `SYS_DOUBLE`, which doubles the `a0` register. This mod uses the ID registry for the syscall and the bridge to reach the VM's registers.

`Double.cs`:

```csharp
using System;
using MISAML.Core;
using MISAML.Godot;

namespace Double;

public static class Main
{
    private static readonly GodotBridge Bridge = new();
    private static int _a0 = -1;

    public static void Start()
    {
        new Ids().RegisterSyscall("SYS_DOUBLE", Double);
    }

    // a0 = a0 * 2
    private static void Double(object vm)
    {
        var registers = (int[])Bridge.GetMember(vm, "_registers")!;

        // look up the register's index once, then reuse
        if (_a0 < 0)
        {
            var regCode = vm.GetType().Assembly.GetType("Asm.General.RegCode")!;
            _a0 = Convert.ToInt32(Enum.Parse(regCode, "A0"));
        }

        registers[_a0] *= 2;
    }
}
```

`mod.json` (manifest)

```json
{
    "modId": "Double",
    "version": "0.1.0",
    "misamlVersion": "^0.1.0",
    "entryAssembly": "Double.dll",
    "entryMethod": "Double.Main.Start"
}
```

Put `Double.dll` and `mod.json` in a folder under `mods/`, then run this program:

```
_start:
    mov a0, 50
    syscall SYS_DOUBLE
    exit
```

When it finishes, `a0` is 100.

The `Start` method is the one named in `entryMethod`. It has to be `public static` with no parameters. The handler never touches Godot, so it doesn't need `OnReady` or `RunOnMainThread`.

## GDScript mods

To enable GDScript mods, give the manifest an `entryScript`. MISAML loads the GDScript once the game has started and adds it to the scene tree, so it has to have `extends Node`. If it extends something else, it loads but never runs, and the log says "script loaded but it isn't a Node".

It also sets two pieces of metadata on the script:

- `get_meta("mod_dir")` is the mod's own folder.
- `get_meta("misaml")` is a callable that calls back into C#.

```gdscript
extends Node

func _ready() -> void:
    var misaml = get_meta("misaml")
    misaml.call("log", "hello from main.gd")
```

`log` is built in and writes to `misaml.log`. To add your own, expose a function from C#.

```csharp
bridge.Expose("double", args => Convert.ToInt32(args[0]) * 2);
```

```gdscript
var result = get_meta("misaml").call("double", 50)
```

`misaml.call(name, args...)` looks the name up in what has been exposed. If nothing is exposed under that name, it throws "nothing exposed as '<name>'". If you expose the same name twice, the later one replaces the earlier one.

If a script doesn't compile, the log says "GDScript failed to compile" and the details are in the game's console. Anything you print with `push_warning` also goes to the console and not `misaml.log`.

A mod can have both an `entryAssembly` and an `entryScript`. The usual split is C# for hooks, syscalls and logic, and GDScript for anything that is easier to do with Godot's own nodes. However, you can do almost everything in C# if you desire.

### Example: colouring custom syscall words in the editor

Words you register with `ids.RegisterSyscall` work in the assembler, but the code editor doesn't know they're syscalls, so it shows them in the plain text colour, which makes using them quite unappealing. This script example gives them the same colour as any built-in syscall:

```gdscript
extends Node

# words to colour like the built-in syscalls
const WORDS := ["SYS_DOUBLE"]

# built-in syscall to copy the colour from, use any
const COLOR_FROM := "SYS_PRINT_INT"

func _ready() -> void:
    get_tree().node_added.connect(_on_node_added)
    _scan(get_tree().root)

# patch editors that already exist
func _scan(node: Node) -> void:
    if node is CodeEdit:
        _patch(node)
    for child in node.get_children():
        _scan(child)

# patch editors created later
func _on_node_added(node: Node) -> void:
    # the editor sets its highlighter just after it is created
    if node is CodeEdit:
        _patch.call_deferred(node)

func _patch(editor: CodeEdit) -> void:
    var highlighter := editor.syntax_highlighter as CodeHighlighter
    if highlighter == null or not highlighter.has_keyword_color(COLOR_FROM):
        return
    var color := highlighter.get_keyword_color(COLOR_FROM)
    for word in WORDS:
        highlighter.add_keyword_color(word, color)
```

How this works:

- The game's editors are Godot `CodeEdit` nodes with a `CodeHighlighter`, which has a colour for each keyword.
- The script patches every `CodeEdit` that already exists (`_scan`), and every one created later (`node_added`). A new editor gets its highlighter just after it is created, which is the reason why the patch is deferred.
- For each word in `WORDS`, the script adds a keyword colour copied from a built-in syscall.

Things to know:

- `WORDS` has to match the words you register in C#, this will be improved later. Add a word there when you add it in `RegisterSyscall`.
- This depends on how the game's editor is built (`CodeEdit`, `CodeHighlighter`, and that built-in word existing). If a game update changes that, the colouring silently stops, and the syscalls still work.
- Colouring is *only* cosmetic. A word that isn't coloured still assembles and runs.

## Building a C# mod

Use .NET 8 (the game is built on it). Reference `MISAML.dll` from a built copy of MISAML, and set `Private` to false so it isn't copied next to your DLL (the game has already loaded it):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="MISAML">
      <HintPath>MISAML.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

This `HintPath` expects you to have placed `MISAML.dll` next to the `*.csproj`. You get `MISAML.dll` from a MISAML build (`dist/MISAML/MISAML.dll`). Change it if yours lives somewhere else.

Build with `dotnet build -c Release`, then put the DLL, `mod.json` and any scripts together in one folder under `mods/`.

If your mod needs other DLLs, put them in the mod folder (or a subfolder). MISAML finds them there. If the game has already loaded a DLL with the same name, the game's copy is used instead, so don't ship your own copy of the game's DLLs. If two mods ship the same DLL, the first one is kept and the log warns you, especially when the versions differ.

To use a game type directly in your code (for example with `hooks.Register<T>`), reference the game's DLL the same way, with `Private` set to false, so the game's own copy is used.

## Using misaml.toml to enable/disable mods

`misaml.toml` sits next to the `mods/` folder. New mods are enabled the first time they're seen, and added to the file. The user can then change them there. The key is the mod's `modId`, and it's not case sensitive:

```toml
[mods]
examplemod = true
othermod = false
tuned = { enabled = true, priority = 50, version = "^1.0.0" }
```

- `enabled` can be left out in the table form. A mod that is listed is on unless it says `enabled = false`.
- `priority` is a whole number from 0 to 65535. Lower priority runs first. This applies to every hook the mod registers, and replaces any priority the mod passes in code.
- `version` is a semver range. If the installed mod doesn't match it, the mod doesn't load.

If the file doesn't exist, then every mod is enabled. If it exists but can't be read, then no mods are loaded at all. MISAML won't guess which ones the user turned off. Fix the file, or delete it and re-run to get a new one with the default configuration.

The same file has launcher settings too:

- `game_dir` sets the game folder by hand, if Steam isn't found.
- `debug` and `force` do the same as the launcher flags.

## Logs

Everything goes to `misaml.log` next to `MISAML.dll`, with the time since the game started. It's cleared on every launch. Messages about a mod start with its `modId`. Set `MISAML_DEBUG=1` for more detail (see step 5 of "Finding methods to hook").

At the end of loading you get a summary line such as `3 of 4 mods loaded (1 not loaded, 0 disabled)`.

## When something goes wrong

Check `misaml.log` first. These are the entries you're most likely to see.

### Loading

| What you see in the log | What it means |
|---|---|
| `no mods directory at ..., skipping` | There's no `mods/` folder where MISAML looked. |
| `skipping <folder>: no mod manifest` | The folder has no `mod.json`. |
| `skipping <folder>: invalid mod manifest (...)` | `mod.json` isn't valid JSON, or a field is wrong. The message says which. |
| `skipping <folder>: mod id '...' is already used by ...` | Two folders have the same `modId`. |
| `mod manifest requires an entry assembly or script` | The manifest has neither `entryAssembly` nor `entryScript`. |
| `assembly not found: ...` | `entryAssembly` names a file that isn't in the mod folder. |
| `class 'X' not found in Y.dll` | `entryMethod` is wrong. It must be `Namespace.Class.Method`. |
| `X.Y() not found (must be public static, no parameters)` | The entry method exists but isn't `public static`, or it takes parameters. |
| `<mod>: disabled, not loading` | It's turned off in `misaml.toml`. |
| `<mod>: not loading, installed version X does not match 'Y' from misaml.toml` | The user pinned a different version. |
| `not loading any mods, because misaml.toml could not be read` | `misaml.toml` has a syntax error. Fix it, or delete it. |
| `made for MISAML x, but this is MISAML y` | A warning only. The mod still loads. |
| `<mod>: loaded OK` / `<mod>: failed to load - ...` | The result for each mod. The text after the dash is the real error. |

### Dependencies

| What you see in the log | What it means |
|---|---|
| `requires X ^1.0.0, which isn't installed (install manually)` | The dependency isn't in `mods/`. The mod gave no `source`. |
| `requires X ^1.0.0, which isn't installed. Get it from <link>` | The same, with a link to where to find it. |
| `requires X ^1.0.0, but 1.2.0 is installed` | The dependency is installed, but the wrong version. |
| `requires X, which was skipped: ...` | The dependency itself didn't load. The reason follows. |
| `dependency cycle: A -> B -> A` | Mods depend on each other in a circle. |

### Hooks

| What you see in the log | What it means |
|---|---|
| `<type>::<method>: successfully registered handler from <mod> (priority N), position X of Y` | MISAML accepted the handler. It doesn't prove the method was found. |
| `failed to register '...'. type, method and handler are all required!` | One of the three was empty or `null`. |
| `<type>::<method> has several overloads: ... Use one of those signatures, or <method>(*)` | You used a bare name on an overloaded method. See "Choosing an overload". |
| `<type>::<method>(...) matches no overload, the ones that exist are: ...` | The signature you wrote doesn't match. Copy one from the message. |
| `... is a static method declared on <type>, register the handler on that type instead` | Register on the type that declares the method. |
| `... is abstract or generic, can't be hooked` / `... is declared on System.Object, refusing to hook it` | MISAML won't hook those. |
| `handler from <mod> for <type>.<method> threw: ...` | Your handler threw. The game carried on without your change. |
| `... returns <type>, but a handler replaced it with <type>, ignoring the replacement` | Your `Replace` value has the wrong type. |

### Syscalls, scripts and Godot

| What you see / what happens | What it means |
|---|---|
| My syscall word isn't recognised in the editor | Check that the mod is enabled and that `RegisterSyscall` returned `true` (it returns `false` if the word is already taken). The words are only added once every mod has loaded. |
| My hook never runs, and nothing is logged | The type name or method name is wrong. Check it with `MISAML_DEBUG=1` (step 5 of "Finding methods to hook"). |
| `GDScript failed to compile` | There's an error in your script. See the game's console. |
| `script loaded but it isn't a Node` | Your script needs `extends Node`. |
| `GDScript failed to start` | The script compiled, but starting it threw. The message follows. |
| `nothing exposed as '<name>'` | A script called `misaml.call` with a name that no C# code has exposed. |
| A Godot call fails at startup | Mods load before the scene tree exists. Move the call into `bridge.OnReady(...)`. |
| `ready handler threw: ...` | Something inside an `OnReady` callback threw. The message follows. |

## Testing your mod(s)

Run the game through `misaml-cli`, do the thing that calls your method, and read `misaml.log`. While you're working things out, log inside your handler (as in the hook example above) and return `HookResult.Continue()`, so the game behaves as normal.

## After a game update

Methods get renamed, moved and changed. If a mod stops working after an update:

1. Look in `misaml.log` for a hook that may have failed.
2. Decompile the new DLLs again.
3. Search for the method by name, then by what it does, since the name may have changed.
4. Update the type name, method name or parameters in your mod.

## Sharing your mod

The easiest way to share a mod is to create a GitHub repository with a release.

**What goes in the repository:** your mod source, `mod.json`, any scripts, and optionally, a `README.md`, and a `LICENSE`. Don't commit build output (`bin/`, `obj/`, the DLL, zip files). Put them in `.gitignore`.

**What goes in the release:** a zip with exactly the files a user needs, and a checksum. It is suggested to include the README.

As an example:

```sh
dotnet build -c Release
zip yourmod-0.1.0.zip YourMod.dll mod.json main.gd LICENSE README.md
sha256sum yourmod-0.1.0.zip > yourmod-0.1.0.zip.sha256
```

Name the files explicitly, as above, so nothing extra (a `bin` folder, a game file you were testing with) ends up in the zip. Check it with `unzip -l yourmod-0.1.0.zip` before you upload it.

On Windows, `Get-FileHash yourmod-0.1.0.zip` in PowerShell prints a SHA256 hash that you can compare with the one in the release.

Then make a GitHub release:

1. Use the same version as in `mod.json` for the tag, and use the same style as your other releases (all with a `v`, or all without).
2. Upload the zip and the `.sha256` file.
3. Say in the notes which MISAML version the mod needs, and which mods it depends on.

With the GitHub CLI, steps 1 to 3 are one command, for an example:

```sh
gh release create 0.1.0 yourmod-0.1.0.zip yourmod-0.1.0.zip.sha256 \
    --target main \
    --title "yourmod 0.1.0" \
    --notes "Needs MISAML 0.1.0 or newer."
```

**Your README should include:** what the mod does, which MISAML version it needs, any dependencies and where to get them, where to unzip it, and the line to add to `misaml.toml` (if it isn't added automatically), etc.

### Don't ship the game's files

Do not, under any circumstances, ship the game's DLLs, shaders, images or other files. They belong to the game's developer. Don't put copies in your mod or your repository. If you need to change one, change it while the game is running and ship only your change. Any mods that ship the game files are not associated with this project and you will not receive assistance with them.

## API reference

A short list of everything a mod can call. The examples earlier in the document show how they are used.

### Hooks

`new Hooks()` (namespace `MISAML.Core`). All instances share the same state.

| Method | Returns | Does |
|---|---|---|
| `Register(string typeName, string methodName, Func<object?, object?[], HookResult> handler)` | nothing | Hooks the method with the default priority (100). |
| `Register(Type type, string methodName, handler)` | nothing | The same, with the type given directly. |
| `Register(string typeName, string methodName, handler, int priority)` | `bool` | Hooks with the priority you give. Lower runs first. |
| `Register(Type? type, string methodName, handler, int priority)` | `bool` | The same, with the type given directly. Needs a type that has a full name. |
| `Register<T>(string methodName, handler, int priority = 100)` | `bool` | The same, using a type parameter. This needs your project to reference the DLL that contains `T`. |
| `Hooks.DefaultPriority` | `int` | 100. |

The `bool` is `false` only if the registration was refused (for example, an empty name). It is `true` even if the method hasn't been found yet. The method name can include a signature, see "Choosing an overload".

The handler gets `(instance, args)`. The instance is `null` for static methods. It must return a `HookResult`.

### HookResult

A struct in `MISAML.Core`.

| Member | Does |
|---|---|
| `HookResult.Continue()` | Lets the game's method run. |
| `HookResult.Replace(object? value)` | Skips the game's method and returns `value` instead. |
| `SkipOriginal`, `Value` | What the result holds. You don't normally need to read these. |

### Ids

`new Ids()` (namespace `MISAML.Core`).

| Method | Returns | Does |
|---|---|---|
| `RegisterSyscall(string word, Action<object> handler)` | `bool` | Adds a syscall word. The handler gets the VM object. |
| `RegisterConstant(string word, long value)` | `bool` | Adds an integer constant word. |
| `RegisterConstant(string word, float value)` | `bool` | Adds a float constant word. |

Each returns `false` if the request is refused (for example, the word is already used). Register in your entry method. MISAML gives the words their numbers after all mods have loaded.

### GodotBridge

`new GodotBridge()` (namespace `MISAML.Godot`).

| Method | Returns | Does |
|---|---|---|
| `Get(object instance, string propertyName)` | `object?` | Reads a Godot property. Nested paths work (`"position:x"`). |
| `Set(object instance, string propertyName, object? value)` | nothing | Sets a Godot property. |
| `Call(object instance, string methodName, params object?[] args)` | `object?` | Calls a Godot method. |
| `CallDeferred(object instance, string methodName, params object?[] args)` | nothing | Calls it at the next idle moment. |
| `Instantiate(string className)` | `object` | Makes a new Godot object. |
| `IsInstanceValid(object instance)` | `bool` | `false` once the object is freed. |
| `Connect(object instance, string signalName, Action<object?[]> handler)` | nothing | Connects a signal. |
| `Disconnect(object instance, string signalName, Action<object?[]> handler)` | nothing | Disconnects it. Pass the same handler. |
| `CreateCallable(Func<object?[], object?> function)` | `object` | Makes a Godot `Callable` out of a C# function. |
| `CallStatic(string typeName, string methodName, params object?[] args)` | `object?` | Calls a static method by full type name. |
| `GetStatic(string typeName, string memberName)` | `object?` | Reads a static field or property. |
| `SetStatic(string typeName, string memberName, object? value)` | nothing | Writes one. |
| `Construct(string typeName, params object?[] args)` | `object` | Makes a new instance of a type. |
| `GetMember(object instance, string name)` | `object?` | Reads a field or property, private ones too. |
| `SetMember(object instance, string name, object? value)` | nothing | Writes one. |
| `CallMember(object instance, string name, params object?[] args)` | `object?` | Calls a method, private ones too. |
| `RunOnMainThread(Action action)` | nothing | Runs the action on the main thread at the next idle moment. Safe from any thread. |
| `OnReady(Action action)` | nothing | Runs the action on the main thread once the game is ready. If the game is already ready, it is queued for the next idle moment on the main thread. |
| `Expose(string name, Func<object?[], object?> function)` | nothing | Lets a GDScript call the function with `misaml.call(name, ...)`. |

A few more notes on the bridge:

- `Connect` has no flags in 0.1.0. Connections are plain.
- Reflection errors come back as the real exception, not a wrapper around it.
- `GodotBridge` also has a `ScriptApi` property and a `RaiseReady()` method. MISAML uses them itself. Mods shouldn't call them.

## Things to know

- **Mods are not sandboxed.** A mod runs with your user's permissions, the same as any program you start. Only install mods you trust.
- **Antivirus.** On Windows the launcher starts the game and loads a DLL into it. Some antivirus software flags that pattern. It is a false positive, but check the source if you aren't sure.
- **Network.** MISAML only uses the network to install the .NET runtime. If anything else is using the network, check your mods.
- **Mac.** Mac support is untested.
- **Game updates.** An update can rename, move or remove the methods and fields your mod uses. Check your mod after every update (see "After a game update").
- **Not in 0.1.0.** MISAML doesn't download dependencies or update mods by itself, and `sourceManifest` isn't used yet. A command-line mod installer and a shared mod API are planned.