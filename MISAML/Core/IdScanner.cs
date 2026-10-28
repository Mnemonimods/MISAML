using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MISAML.Core.Records;
using MISAML.Interfaces;

namespace MISAML.Core;

/// <summary>
/// Reflects over the game's own assembly to find every number and word it
/// already uses and has reserved, so <see cref="IdRegistry.Allocate"/> knows
/// which ones mods aren't allowed to claim.
/// </summary>
public class IdScanner(ILogger<IdScanner> logger) : IIdScanner
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private const string EnumMaps = "Asm.General.EnumMaps";
    private const string Lexer = "Asm.Frontend.Lexer";

    /// <summary>
    /// Where one source of used numbers lives, a static dictionary in the game.
    /// </summary>
    /// <param name="Type">The full name of the type that holds the dictionary.</param>
    /// <param name="Member">The name of the static field or property holding the dictionary.</param>
    /// <param name="Keys">Boolean, true to pull the numbers from the dict's keys,
    /// false to pull them from its values.</param>
    private record Source(string Type, string Member, bool Keys);

    // one ID space, enum that defs the games own numbers, and maps that hold the nums
    /// <summary>
    /// One ID space, the enum that defines the game's own numbers for it, and
    /// every dict that also holds numbers from that space.
    /// </summary>
    /// <param name="Space">Which ID space this definition is for.</param>
    /// <param name="EnumType">The full name of the enum that defines the game's
    /// own numbers in this space.</param>
    /// <param name="Sources">Every other static dictionary that also holds
    /// numbers from this space.</param>
    private record SpaceDef(IdSpace Space, string EnumType, Source[] Sources);

    /// <summary>
    /// Every ID space the scanner knows how to find, and where to find its numbers.
    /// </summary>
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

    /// <summary>
    /// Scans the game's loaded assembly for every number and word it already
    /// uses, across every ID space.
    /// </summary>
    /// <returns>Every number and word the game already uses. Empty if the game's
    /// Asm assembly isn't loaded yet, individual spaces come back missing numbers
    /// if a map got renamed.</returns>
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

    /// <summary>
    /// Every number an enum defines, take the type by name.
    /// </summary>
    /// <param name="asm">The assembly to look up the enum type in.</param>
    /// <param name="typeName">The full name of the enum type.</param>
    /// <returns>Every value the enum defines, or empty if the type can't be
    /// found.</returns>
    private static IEnumerable<int> EnumValues(Assembly asm, string typeName)
    {
        var type = asm.GetType(typeName);
        if (type == null) return [];
        return Enum.GetValues(type).Cast<object>().Select(o => Convert.ToInt32(o));
    }

    /// <summary>
    /// A static dictionary field (or property) read as a plain IDictionary so
    /// we do not require the game's generic types.
    /// </summary>
    /// <param name="asm">The assembly to look the type up in.</param>
    /// <param name="typeName">The full name of the type that holds the dictionary.</param>
    /// <param name="member">The name of the static field or property holding the dictionary.</param>
    /// <returns>The dictionary, or null if the type, member, or value isn't what's expected.</returns>
    internal static IDictionary? Map(Assembly asm, string typeName, string member)
    {
        var type = asm.GetType(typeName);
        var value = type?.GetField(member, AnyStatic)?.GetValue(null)
            ?? type?.GetProperty(member, AnyStatic)?.GetValue(null);
        return value as IDictionary;
    }
}