using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using MISAML.Core;
using MISAML.Godot;
using MISAML.Interfaces;

namespace MISAML.Bootstrap;

// WHY DOES THIS CLASS/ASSEMBLY EXIST?
//
// MISAML.dll gets loaded into the game's process by misaml-shim, via a raw
// CoreCLR hosting call (coreclr_create_delegate). This always loads the
// assembly in the ".NET Default" AssemblyLoadContext, giving our (MISAML's)
// code and types their own "isolated" managed runtime context within the
// game's process.
//
// Godot's own native bootstrap, seperately, loads Mnemonimov.dll, Asm.dll,
// GodotSharp.dll, and other manage assemblies into a different "isolated" 
// runtime context:
// "Internal.Runtime.InteropServices.IsolatedComponentLoadContext".
// This is separate from the ".NET Default" AssemblyLoadContext used by MISAML.
//
// This matters because as I painfully and tediously learnt through trial and
// error... .NET treats a type (like Godot.GodotObject) as a genuinely
// different type per room it's loaded into, even if it's the exact same file,
// loaded twice.
//
// This was confirmed as two Assembly objects for the identical GodotSharp.dll 
// file path failed ReferenceEquals (returned False). A plain
// "is Godot.GodotObject" check, against a real, live game object, also
// returned False, when checked using a GodotObject type resolved from this
// (Default) context.
//
// tl;dr, the shim and Godot load assemblies into different closed rooms,
// and sending messages between these rooms isn't exactly possible.
//
// Plain method calls on live objects still work fine from Default context, as 
// they don't need two types to be "the same", just any real object. 
// But anything concerning a type identity check (is/as/casts) against a live
// Godot object do not and will not work reliably from Default context.
//
// THE FIX THIS CLASS IMPLEMENTS
//
// AssemblyLoadContext has a public method that allows us to load an assembly
// into a specific "room", from ordinary C# code, no low-level stuff required.
// So, once Mnenimov.dll, Asm.dll, GodotSharp.dll, etc. are confirmed loaded,
// we just grab their room, and then load MISAML.dll into that room. This
// is ONLY possible through C# code for some godforsaken reason.
//
// So this is essentially just bootstrap code, required for MISAML.dll to
// resolve things properly while avoiding reflection, hopefully being more
// straightforward to work with, as we can use native Godot API's.
//
// The bootstrap only does what is described above.

public class Loader(IGodotBridge godotBridge, IModLoader modLoader, ICore startupHook)
{
    public static Loader? Instance;

    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(Loader).Assembly.Location)!,
        "misaml.log"
    );

    private static void Log(string message)
    {
        var line = $"{_stopwatch.Elapsed.TotalSeconds:F6} [MISAML.Bootstrap] {message}";
        File.AppendAllText(LogPath, line + "\n");
    }

    // wait to load either of these before init
    private static readonly string[] AssemblyNames = ["GodotSharp", "Mnemonimov"];

    // guards against initializing more than once if both load & trigger AssemblyLoad before
    // the first handoff finishes
    private static int _handoffStarted = 0;

    // Called once, directly by the Rust shim via coreclr_create_delegate
    public static void Handoff()
    {
        File.WriteAllText(LogPath, ""); // clear log

        Log("Starting, waiting for Mnemonimov...");

        Instance = new Loader(
            new GodotBridge(),
            new ModLoader(
                new GodotBridge(),
                new Hooks(),
                new ModSettings(),
                new VersionReq(),
                new DependencyResolver()
                ),
            new Core.Core(new GodotBridgePatch())
        );

        AppDomain.CurrentDomain.AssemblyLoad += Instance.OnAssemblyLoad;
    }

    private void Start(Assembly anchorAssembly)
    {
        try
        {
            // Harmony setup runs directly, in Default context, no context
            // switching. Harmony patches VirtualMachine.HandleSystemCall,
            // the 58 bridge methods, all others, etc. etc.
            Log("calling StartupHook.Initialize() directly...");
            startupHook.Initialize();
            Log("StartupHook.Initialize() returned");

            // Load MISAML.GodotAPI.dll separately, into the isolated context
            // to interface with the Godot API directly.
            var isolatedContext = AssemblyLoadContext.GetLoadContext(anchorAssembly)
                                  ?? throw new Exception("couldn't get load context for " + anchorAssembly);

            Log($"got isolated context: {isolatedContext.Name}");

            var payloadPath = Path.Combine(
                Path.GetDirectoryName(typeof(Loader).Assembly.Location)!,
                "MISAML.GodotAPI.dll"
            );

            if (!File.Exists(payloadPath))
                throw new Exception($"MISAML.GodotAPI.dll not found at {payloadPath}");

            var isolatedPayload = isolatedContext.LoadFromAssemblyPath(payloadPath);
            GodotBridge.Bind(isolatedPayload);

            // fire OnReady the first time the game calls into one of its bridge methods
            GodotBridgePatch.OnBridgeInstanceSeen += OnFirstBridgeCall;

            var baseDir = Path.GetDirectoryName(typeof(Loader).Assembly.Location)!;
            var modsDir = Path.GetFullPath(Path.Combine(baseDir, "..", "mods"));
            modLoader.GameContext = isolatedContext;
            modLoader.LoadMods(modsDir, Log);
        }
        catch (Exception e)
        {
            Log("Start failed: " + e);
        }
    }

    private void OnFirstBridgeCall(object instance)
    {
        // game is running, tree exists
        GodotBridgePatch.OnBridgeInstanceSeen -= OnFirstBridgeCall;
        godotBridge.RaiseReady();
    }

    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        string? loadedName = args.LoadedAssembly.GetName().Name;
        if (loadedName == null || !AssemblyNames.Contains(loadedName))
            return;

        // only the first anchor assembly triggers the handoff
        if (Interlocked.CompareExchange(ref _handoffStarted, 1, 0) != 0)
            return;

        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

        Log($"anchor assembly '{loadedName}' loaded, starting handoff...");
        Start(args.LoadedAssembly);
    }
}