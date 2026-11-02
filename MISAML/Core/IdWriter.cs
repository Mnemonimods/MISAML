using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MISAML.Core.Records;
using MISAML.Interfaces;

namespace MISAML.Core;

/// <summary>
/// Takes the numbers <see cref="IdRegistry.Allocate"/> handed out and writes
/// them into the game's own assembler types and lexer maps via reflection,
/// then hooks the VM/parser so the new syscalls and constants resolve at runtime.
/// </summary>
/// <param name="hooks">Used to hook the game's syscall dispatch and constant resolution.</param>
public class IdWriter(IHooks hooks, ILogger<IdWriter> logger) : IIdWriter
{
    /// <summary>
    /// Writes every allocation into the game's lexer/enum maps, and hooks the
    /// VM so syscalls and constants resolve at runtime.
    /// </summary>
    /// <param name="allocations">The allocations to apply, as handed out by <see cref="IdRegistry.Allocate"/>.</param>
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

    /// <summary>
    /// Hook for VirtualMachine.HandleSystemCall, runs a mod's handler when the
    /// VM hits one of the registered syscall codes.
    /// </summary>
    /// <param name="vm">The running VirtualMachine instance, passed through to the handler.</param>
    /// <param name="args">The hooked method's original arguments, `args[0]` is the ScCode being run.</param>
    /// <param name="handlers">Every registered syscall's handler, keyed by its own code.</param>
    /// <returns><see cref="HookResult.Continue"/> if `args[0]` is missing or
    /// isn't a registered syscall. Otherwise <see cref="HookResult.Replace"/>
    /// with null, since the original method would treat an unknown as an error either way.</returns>
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

    /// <summary>
    /// Hook for `BuiltInConstant.Resolve`, gives a registered constant its value
    /// when the parser resolves it.
    /// </summary>
    /// <param name="self">The `BuiltInConstant` instance being resolved.</param>
    /// <param name="number">The game's `Number` type, used to construct
    /// the resolved value.</param>
    /// <param name="constants">Every registered constant's value, keyed by its token.</param>
    /// <returns><see cref="HookResult.Continue"/> if this constant's token isn't
    /// one a registered one, otherwise <see cref="HookResult.Replace"/> with
    /// a new `Number` built from its value.</returns>
    private static HookResult ResolveConstant(object? self, Type number, Dictionary<int, object> constants)
    {
        var token = self?.GetType().GetProperty("Token")?.GetValue(self);
        var type = token?.GetType().GetProperty("Type")?.GetValue(token);

        if (type == null || !constants.TryGetValue(Convert.ToInt32(type), out var value))
            return HookResult.Continue();

        // Number takes a long or a float
        return HookResult.Replace(Activator.CreateInstance(number, value));
    }

    /// <summary>
    /// Finds a type in the given assembly by its short name, ignoring
    /// namespace, since it is not known which namespace a type ends up in
    /// (as of v0.1.0).
    /// </summary>
    /// <param name="asm">The assembly to search.</param>
    /// <param name="name">The type's short name (no namespace).</param>
    /// <returns>The type, or null if it isn't found or the assembly can't fully
    /// load its types.</returns>
    private static Type? FindType(Assembly asm, string name)
    {
        try { return asm.GetTypes().FirstOrDefault(t => t.Name == name); }
        catch (ReflectionTypeLoadException) { return null; }
    }
}