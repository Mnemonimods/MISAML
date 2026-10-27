using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MISAML.Core.Records;
using MISAML.Interfaces;

namespace MISAML.Core;

public class IdScanner(ILogger<IdScanner> logger) : IIdScanner
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private const string EnumMaps = "Asm.General.EnumMaps";
    private const string Lexer = "Asm.Frontend.Lexer";

    // where one src of used nums lives, static dict ingame
    private record Source(string Type, string Member, bool Keys);

    // one ID space, enum that defs the games own numbers, and maps that hold the nums
    private record SpaceDef(IdSpace Space, string EnumType, Source[] Sources);

    private static readonly SpaceDef[] Spaces =
    [
        new(IdSpace.Token, "Asm.Frontend.TkType",
        [
            new(EnumMaps, "TkTypeToOpCode", true),
            new(EnumMaps, "TkTypeToRegCode", true),
            new(EnumMaps, "TkTypeToCondCode", true),
            new(EnumMaps, "TkTypeToDtCode", true),
            new(EnumMaps, "TkTypeToScCode", true),
            new(Lexer, "_lexemeToTkType", false),
            new(Lexer, "_tkTypeToTkCategory", true),
        ]),
        new (IdSpace.SyscallCode, "Asm.General.ScCode",
        [
            new(EnumMaps, "TkTypeToScCode", false),
        ]),
        new(IdSpace.OpCode, "Asm.General.OpCode",
        [
            new(EnumMaps, "TkTypeToOpCode", false),
            new(EnumMaps, "OpCodeToGenOpCode", true),
            new(EnumMaps, "OpCodeToGenOpCode", false),
            new(EnumMaps, "OpCodeToInstSize", true),
        ]),
        new(IdSpace.RegCode, "Asm.General.RegCode",
        [
            new(EnumMaps, "TkTypeToRegCode", false),
        ]),
        new(IdSpace.CondCode, "Asm.General.CondCode",
        [
            new(EnumMaps, "TkTypeToCondCode", false),
        ]),
        new(IdSpace.DtCode, "Asm.General.DtCode",
        [
            new(EnumMaps, "TkTypeToDtCode", false),
            new(EnumMaps, "DtCodeToSize", true),
        ]),
    ];

    public UsedIds Scan()
    {
        var numbers = Spaces.ToDictionary(s => s.Space, _ => new HashSet<int>());
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new UsedIds(numbers, words);

        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Asm");
        if (asm == null)
        {
            logger.LogWarning("couldn't scan the game's IDs: the Asm assembly isn't loaded yet");
            return used;
        }

        foreach (var space in Spaces)
        {
            var set = numbers[space.Space];

            // the enum holds every num the game defines itself, including ones
            // that no map mentions
            set.UnionWith(EnumValues(asm, space.EnumType));

            foreach (var source in space.Sources)
            {
                var map = Map(asm, source.Type, source.Member);
                if (map == null)
                {
                    // game updates can rename maps, warn instead of quiet failure
                    logger.LogWarning($"couldn't find {source.Type}.{source.Member}, {space.Space} numbers from it are missing");
                    continue;
                }

                foreach (var item in source.Keys ? map.Keys : map.Values)
                    set.Add(Convert.ToInt32(item)); // needs for MISA
            }
        }

        if (Map(asm, Lexer, "_lexemeToTkType") is { } lexemes)
        {
            foreach (string word in lexemes.Keys) words.Add(word);
        }

        return used;
    }

    // every num an enum defs, take the type by name
    private static IEnumerable<int> EnumValues(Assembly asm, string typeName)
    {
        var type = asm.GetType(typeName);
        if (type == null) return [];
        return Enum.GetValues(type).Cast<object>().Select(o => Convert.ToInt32(o));
    }

    // a static dict field (or prop) read as plain IDict so we don't need game
    // generic types
    internal static IDictionary? Map(Assembly asm, string typeName, string member)
    {
        var type = asm.GetType(typeName);
        var value = type?.GetField(member, AnyStatic)?.GetValue(null)
            ?? type?.GetProperty(member, AnyStatic)?.GetValue(null);
        return value as IDictionary;
    }
}