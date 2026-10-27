using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MISAML.Core.Records;
using MISAML.Interfaces;

namespace MISAML.Core;

// mods ask for a word by name while loading (syscall or token), numbers are 
// given out later in one pass (Allocate), once every request is in, so a 
// mod's hard coded numbers are already in the game's maps and get skipped
public class IdRegistry(ILogger<IdRegistry> logger) : IIdRegistry
{
    private readonly object _lock = new();

    // one registered word
    private record Request(IdKind Kind, Action<object>? Handler, object? Value);

    // word -> handler, null handler means token w/ no syscall, case-insensitive
    // as the assembler ignores case in words
    private readonly Dictionary<string, Request> _requests
        = new(StringComparer.OrdinalIgnoreCase);

    // the same rule the lexer uses for identifiers (Lexer.RegexIdentifier)
    private static readonly Regex WordPattern = new(@"^(?!_*$)[a-zA-Z_][a-zA-Z0-9_]*\z", RegexOptions.Compiled);

    private bool _closed;

    // the highest number a space can hold, Syscall codes are 2 bytes in the
    // bytecode, opcodes, conditions and data types are 1 byte, Registers are 
    // null: a register is a 1 byte index into the VM's register array, so 
    // adding one means resizing that array, not just taking a number
    private static int? Limit(IdSpace space) => space switch
    {
        IdSpace.Token => int.MaxValue,
        IdSpace.SyscallCode => 65535,
        IdSpace.OpCode or IdSpace.CondCode or IdSpace.DtCode => 255,
        _ => null,
    };
    //TODO: figure out how to raise the mf'ing opcode size because that SUCKS.


    public bool RegisterSyscall(string word, Action<object> handler)
        => Add(word, new Request(IdKind.Syscall, handler, null));

    public bool RegisterConstant(string word, long value)
        => Add(word, new Request(IdKind.Constant, null, value));

    public bool RegisterConstant(string word, float value)
        => Add(word, new Request(IdKind.Constant, null, value));

    // false (and says why) if the request is refused
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

    // lowest free num in a space, claims it so the next call skips it, Null
    // (and syas why) when the sapce is full or its limit is unknown
    // HashSet.Add returns false when num is already there, remember?
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

    // closes registration and gives every request its nums, addng them
    // to used, sorted by word
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