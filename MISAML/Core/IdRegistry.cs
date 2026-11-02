using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MISAML.Core.Records;
using MISAML.Interfaces;

namespace MISAML.Core;

/// <summary>
/// Mods ask for a word by name while loading (syscall or token), numbers are
/// given out later in one pass (Allocate), once every request is in, so a mod's
/// hard-coded numbers are already in the game's maps and get skipped.
/// </summary>
public class IdRegistry(ILogger<IdRegistry> logger) : IIdRegistry
{
    private readonly object _lock = new();

    /// <summary>
    /// A single registered word.
    /// </summary>
    /// <param name="Kind">The kind of the word, a syscall, or a plain constant token.</param>
    /// <param name="Handler">The syscall's handler, or null for a token with no syscall.</param>
    /// <param name="Value">The constant's value (long or float boxed as object), or null for a syscall.</param>
    private record Request(IdKind Kind, Action<object>? Handler, object? Value);

    // OrdinalIgnoreCase on purpose: the lexer is case sensitive, but the game
    // reserves look-alike words (TRUE/true)
    /// <summary>
    /// Registered words
    /// </summary>
    private readonly Dictionary<string, Request> _requests
        = new(StringComparer.OrdinalIgnoreCase);

    // the same rule the lexer uses for identifiers (Lexer.RegexIdentifier)
    private static readonly Regex WordPattern = new(@"^(?!_*$)[a-zA-Z_][a-zA-Z0-9_]*\z", RegexOptions.Compiled);

    private bool _closed;

    /// <summary>
    /// The highest number a space can hold. Syscall codes are 2 bytes in the
    /// bytecode. Opcodes, conditions and data types are 1 byte, Registers are
    /// null. A register is a 1 byte index into the VM's register array.
    /// </summary>
    /// <param name="space">Which ID space to look up the limit for.</param>
    /// <returns>The highest number allowed in that space, or null if the space
    /// isn't limited this way.</returns>
    private static int? Limit(IdSpace space) => space switch
    {
        IdSpace.Token => int.MaxValue,
        IdSpace.SyscallCode => 65535,
        IdSpace.OpCode or IdSpace.CondCode or IdSpace.DtCode => 255,
        _ => null,
    };
    //TODO: figure out how to raise the mf'ing opcode size because that SUCKS.

    /// <summary>
    /// Registers a word as a syscall.
    /// </summary>
    /// <param name="word">The word used to call this syscall. Must match
    /// <see cref="WordPattern"/>.</param>
    /// <param name="handler">The handler to run when this syscall is invoked.</param>
    /// <returns>bool, false if the request is refused.</returns>
    public bool RegisterSyscall(string word, Action<object> handler)
        => Add(word, new Request(IdKind.Syscall, handler, null));

    /// <summary>
    /// Registers a word as a long constant.
    /// </summary>
    /// <param name="word">The word used to refer to this constant. Must match
    /// <see cref="WordPattern"/>.</param>
    /// <param name="value">The constant's value, long.</param>
    /// <returns>bool, false if this request is refused.</returns>
    public bool RegisterConstant(string word, long value)
        => Add(word, new Request(IdKind.Constant, null, value));

    /// <summary>
    /// Registers a word as a float constant.
    /// </summary>
    /// <param name="word">The word used to refer to this constant. Must match
    /// <see cref="WordPattern"/>.</param>
    /// <param name="value">The constant's value, float.</param>
    /// <returns>bool, false if this request is refused.</returns>
    public bool RegisterConstant(string word, float value)
        => Add(word, new Request(IdKind.Constant, null, value));

    /// <summary>
    /// Shared path for every registration. Checks the word, checks that
    /// registrations are still open, checks that a word hasn't already
    /// been registered, before adding it to <see cref="_requests"/>.
    /// </summary>
    /// <param name="word">The word being registered.</param>
    /// <param name="request">The request to store for this word.</param>
    /// <returns>bool, false if the request is refused.</returns>
    private bool Add(string word, Request request)
    {
        lock (_lock)
        {
            if (!WordPattern.IsMatch(word))
            {
                logger.LogError($"can't register '{word}': words can only have letters, digits and underscores, and can't start with a digit");
                return false;
            }

            if (_closed)
            {
                logger.LogError($"can't register '{word}': registration is closed, register while the mod loads");
                return false;
            }

            if (!_requests.TryAdd(word, request))
            {
                logger.LogError($"can't register '{word}': another mod or the game already registered this word");
                return false;
            }

            return true;
        }
    }

    // HashSet.Add returns false when num is already there, remember?
    /// <summary>
    /// Finds and claims the lowest free number in a space so the next call
    /// skips it.
    /// </summary>
    /// <param name="used">The numbers already claimed, across every space.</param>
    /// <param name="space">Which space to claim a number from.</param>
    /// <returns>null when the space is full or its limit is unknown.</returns>
    private int? Claim(UsedIds used, IdSpace space)
    {
        if (Limit(space) is not { } limit)
        {
            logger.LogError($"can't give out {space} numbers yet");
            return null;
        }

        var numbers = used.Of(space);
        var n = 0;
        while (!numbers.Add(n)) n++;

        if (n > limit)
        {
            numbers.Remove(n);
            logger.LogError($"all {space} numbers are used");
            return null;
        }

        return n;
    }

    /// <summary>
    /// Closes registration and gives every request its numbers, adding them
    /// to <paramref name="used"/>, sorfed by word.
    /// </summary>
    /// <param name="used">The numbers already claimed elsewhere, this gets updated
    /// with everything claimed here.</param>
    /// <returns>The allocation for every request that got numbers, sorted by word.
    /// Requests that failed are skipped.</returns>
    public IReadOnlyList<Allocation> Allocate(UsedIds used)
    {
        lock (_lock)
        {
            _closed = true;
            var result = new List<Allocation>();

            foreach (var (word, request) in _requests.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (used.Words.Contains(word))
                {
                    logger.LogError($"can't give '{word}' a number, the game or a mod already uses this word");
                    continue;
                }

                // only syscalls need a code
                int? code = null;
                if (request.Kind == IdKind.Syscall)
                {
                    code = Claim(used, IdSpace.SyscallCode);
                    if (code == null) continue;
                }

                if (Claim(used, IdSpace.Token) is not { } token) continue;

                result.Add(new Allocation(word, request.Kind, token, code, request.Handler, request.Value));
            }

            return result;
        }
    }
}