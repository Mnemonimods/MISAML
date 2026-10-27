using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MISAML.Core.Records;
using MISAML.Interfaces;

namespace MISAML.Core;

public class IdWriter(IHooks hooks, ILogger<IdWriter> logger) : IIdWriter
{
    public void Apply(IReadOnlyList<Allocation> allocations)
    {
        if (allocations.Count == 0) return;

        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Asm");
        var tkType = asm?.GetType("Asm.Frontend.TkType");
        var tkCategory = asm?.GetType("Asm.Frontend.TkCategory");
        var scCode = asm?.GetType("Asm.General.ScCode");
        var lexemes = asm == null ? null : IdScanner.Map(asm, "Asm.Frontend.Lexer", "_lexemeToTkType");
        var categories = asm == null ? null : IdScanner.Map(asm, "Asm.Frontend.Lexer", "_tkTypeToTkCategory");
        var codes = asm == null ? null : IdScanner.Map(asm, "Asm.General.EnumMaps", "TkTypeToScCode");

        if (asm == null || tkType == null || tkCategory == null || scCode == null
            || lexemes == null || categories == null || codes == null)
        {
            logger.LogError("can't write the registered words into the game: part of the assembler wasn't found, see the earlier warnings");
            return;
        }

        var handlers = new Dictionary<int, Action<object>>();
        var constants = new Dictionary<int, object>();

        foreach (var a in allocations)
        {
            try
            {
                var token = Enum.ToObject(tkType, a.Token);
                var category = Enum.Parse(tkCategory, a.Kind == IdKind.Syscall ? "SystemCall" : "BuiltInConstant");

                categories.Add(token, category);

                if (a.Kind == IdKind.Syscall)
                {
                    codes.Add(token, Enum.ToObject(scCode, a.Code!.Value));
                    handlers[a.Code.Value] = a.Handler!;
                }
                else
                {
                    constants[a.Token] = a.Value!;
                }

                lexemes.Add(a.Word, token);
                logger.LogInformation($"registered {a.Kind.ToString().ToLowerInvariant()} '{a.Word}' (token {a.Token}{(a.Code is { } c ? $", code {c}" : "")})");
            }
            catch (Exception e)
            {
                logger.LogError(e, $"failed to register '{a.Word}': {e.Message}");
            }
        }

        if (handlers.Count > 0)
        {
            hooks.Register("Asm.Running.VirtualMachine", "HandleSystemCall",
                (vm, args) => RunSyscall(vm, args, handlers));
        }

        if (constants.Count > 0)
        {
            var number = FindType(asm, "Number");
            if (number == null)
                logger.LogError("can't give constants their values: the Number type wasn't found");
            else
                hooks.Register("Asm.Frontend.BuiltInConstant", "Resolve",
                    (self, _) => ResolveConstant(self, number, constants));
        }
    }

    // args[0] is the ScCode being run
    private HookResult RunSyscall(object? vm, object?[] args, Dictionary<int, Action<object>> handlers)
    {
        if (args.Length == 0 || args[0] == null) return HookResult.Continue();

        var code = Convert.ToInt32(args[0]);
        if (!handlers.TryGetValue(code, out var handler)) return HookResult.Continue();

        try
        {
            handler(vm!);
        }
        catch (Exception e)
        {
            logger.LogError(e, $"syscall {code} threw: {e.Message}");
        }

        // replaced either way, the original would treat an unknown code as an error
        return HookResult.Replace(null);
    }

    private static HookResult ResolveConstant(object? self, Type number, Dictionary<int, object> constants)
    {
        var token = self?.GetType().GetProperty("Token")?.GetValue(self);
        var type = token?.GetType().GetProperty("Type")?.GetValue(token);

        if (type == null || !constants.TryGetValue(Convert.ToInt32(type), out var value))
            return HookResult.Continue();

        // Number takes a long or a float
        return HookResult.Replace(Activator.CreateInstance(number, value));
    }

    private static Type? FindType(Assembly asm, string name)
    {
        try { return asm.GetTypes().FirstOrDefault(t => t.Name == name); }
        catch (ReflectionTypeLoadException) { return null; }
    }
}