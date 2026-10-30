using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MISAML.Bootstrap.Interfaces;
using MISAML.Core;
using MISAML.Debug;
using MISAML.Debug.Interfaces;
using MISAML.Godot;
using MISAML.Interfaces;
using Serilog;

namespace MISAML.Bootstrap;

public class DependencyInjector : IDisposable
{
    private readonly string logPath = Path.Combine(
        Path.GetDirectoryName(typeof(DependencyInjector).Assembly.Location)!
        , "misaml.log"
    );

    public ServiceProvider Services { get; private set; } = null!;

    public void Initialize()
    {
        try { File.WriteAllText(logPath, ""); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var serilogLogger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(logPath,
                retainedFileCountLimit: null)
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            if (Core.Core.DebugEnabled)
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddFilter("MISAML", LogLevel.Debug);
            }
            else
            {
                logging.SetMinimumLevel(LogLevel.Information);
            }
            logging.AddSerilog(serilogLogger, dispose: true);
        });

        services.AddSingleton<IGodotBridge, GodotBridge>();
        services.AddSingleton<IGodotBridgePatch, GodotBridgePatch>();
        services.AddSingleton<ICore, Core.Core>();
        services.AddSingleton<IHooks, Hooks>();
        services.AddSingleton<IModLoader, ModLoader>();
        services.AddTransient<IModSettings, ModSettings>();
        services.AddTransient<IVersionReq, VersionReq>();
        services.AddSingleton<IDependencyResolver, DependencyResolver>();
        services.AddSingleton<Loader>();
        services.AddSingleton<GodotBridgePatch>();
        services.AddSingleton<IDebug, Debug.Debug>();
        services.AddSingleton<IBridge, Bridge>();
        services.AddSingleton<IGodotSharp, GodotSharp>();
        services.AddSingleton<IMnemonimov, Mnemonimov>();
        services.AddSingleton<IAsm, Asm>();
        services.AddSingleton<IIdScanner, IdScanner>();
        services.AddSingleton<IIdRegistry, IdRegistry>();
        services.AddSingleton<IIdWriter, IdWriter>();
        services.AddSingleton<IIds, Ids>();

        Services = services.BuildServiceProvider();

        // nothing asks for/uses IIds, but building it is what hands
        // mods the reg and it has to exist before any mods load
        Services.GetRequiredService<IIds>();
    }

    public void Dispose() => Services?.Dispose();
}