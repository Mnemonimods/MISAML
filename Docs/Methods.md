# MISAML Patchable Methods Reference

Game version checked against: 0.1.6 (2026-08-31)
Last checked: 2026-10-08
DLL hashes at time of checking:
    Mnemonimov.dll:  5111b0825cb5919c5a8fcc30717b2c0f
    Asm.dll:         173d587f1dd59340138f8bb06f7f1f7c
    GodotSharp.dll:  16dbe046b79ca898d2debefb1709906d

**NOTE: Some types below are CLR names from .NET reflection, not C# keywords, e.g. Boolean instead of bool.**

## How to update this document

1. Launch the game with MISAML_DEBUG=1.
2. Let the search finish.
3. Pull every OK| line out of misaml-debug.log and sort into the
   sections below by label, then by type.
4. Re-run md5sum on the three DLLs above and update the hashes and date.
5. Edit any notes on Methods that may have been updated.

## Bridge: Mnemonimov.src.asm.*

Mnemonimov's confirmed `[GlobalClass]` bridge types. These are the "designed to be scripted" surfaces. Start here first for most mods.

> **Public vs. private matters here because of how the methods are patched.**
>
> **Public methods:** `GodotBridgePatch` automatically patches every public method on these types when the game loads. If the debug sweep reports `already patched elsewhere!`, it means the bootstrap has already handled it, **you don't need to patch it yourself**.
>
> **Private/internal methods:** The bootstrap only looks at `BindingFlags.Public`, so these methods are not automatically patched. They're still visible to the debug sweep, but if you want to intercept one, **you need to apply your own Harmony patch**.
>
> Private methods also **can't be called normally through Variant/GDScript** because they're not publicly accessible. You can patch/intercept them, but you can't expose them as a normal callable method through the public API.

### VirtualMachineRunner

The game's main bridge node for VM lifecycle and execution control. Thin wrapper over Asm.Running.VirtualMachine, see that type's notes in the Asm section for real implementation.

#### Lifecycle

- `Boolean Initialize(String storageFile, Int32 processCount, Int32 processUnitaryStackSize, Int32 memorySize, Byte[] program, Int32[] relativeInstructionAddresses)`. Forwards direclty to `_virtualMachine.Initialize`.
- `Void DestroyWrappers()`. Disposes the Godot-side rendering wrappers (Image, ImageTexture) and the node itself. Does NOT reset `_virtualMachine`'s internal state (registers, memory, etc.). This only tears down rendering/node resources, not the VM.
- `Void Run()`. Forwards to `_virtualMachine.Run(isRunThreaded: true)`. Note how the bridge takes `isRunThreaded` hardcoded here, the real VM's `Run()` takes a Boolean to choose threaded vs. non-threaded, but you cannot pick this from the Bridge layer, only `Asm.dll`'s `Run(Boolean)` lets you pick this.
- `Void Lock()` / `Void Unlock()`. Forward directly to `_virtualMachine.Lock()/Unlock()`. A manual lock around the VM state.
- `Void Stop()`. Pure forwarding to `_virtualMachine.Stop()`.

#### Execution control

- `Void ResetFrameCpuBudget(Double factor)`. Pure forwarding, scales the VM's per-frame CPU instruction budget by factor.
- `Void SpawnBuiltInProcess(Int32 builtInProcessEnumID)`. Casts the raw int to a BuiltInprocess enum, then forwards. The bridge exposes this as a bare int, check `Asm.dll`'s BuiltInProcess enum for valid values.
- `Void SetIsPaused(Boolean isPaused)`. Pure forwarding to `_virtualMachine.SetIsPaused(isPaused)`.
- `Boolean IsRunning()`. Pure forwarding to `_virtualMachine.IsRunning()`.
- `Boolean IsPaused()`. Forwards to `_virtualMachine.IsCurrentlyPaused()`, different names, take note.

#### Debug stepping

- `Void StepOver()` / `Void StepInto()` / `Void StepOut()`. All three forward to the *same* underlying method, `_virtualMachine.SetStep(VirtualMachine.Step.Over/Into/Out)`. If you want one hook point to catch all stepping instead of patching all three Bridge methods separately, patch SetStep(Step) directly.
- `Boolean IsStepping()`. Pure forwarding of `_virtualMachine.IsStepping()`.
- `Void SetBreakpointAddresses(Int32[] addresses)`. Pure forwarding of `_virtualMachine.SetBreakPointAddresses(addresses)`.

#### State inspection / telemetry

- `Int32 GetProgramAddress()`. Reads `_virtualMachine.ProgramAddress`, a plain field, it's the fixed base address where the currently loaded program's bytecode starts in VM memory, set once and shared by every process (all processes run the same loaded program). Defaults to -1 before a program is loaded. Does *not* change during execution. See `GetAddress()` below for that.
- `Int32 GetAddress()`. Forwards to `_virtualMachine.GetAddress()`, which reads `Reg(RegCode.Pc)`, the *current* PC of the active process. Changes every instruction as the program runs. Returns -1 if there's no active process.
- `Int32 GetUnhandledExceptionAddress()`. Reads the `_unhandledExceptionAddress` field (lock-protected). Set by `VM.Panic()` when execution hits an unhandled error. Check Panic's own definition for more.
- `Int32 GetCycleCount()`. Reads the `_cycleCounter` field (lock-protected), total instruction cycles executed so far by the active process.
- `Int32[] GetStackTraceAddresses()`. Walks the call stack by following the frame pointer (Fp register) chain backward through memory, collecting each caller's address into a list. Effectively a backtrace. Each stored address is adjusted backward by the size of a `Cal` (call) instruction, so what you get is the address of each *call site* itself, not the return address (instruction after the call). Empty array if there's no active processes. Returns `[-1]` (a single element array, not empty) if something goes wrong walking the call stack.This distinguishes "no process" from "trace failed partway through," worth checking for this specifically rather than just checking array length == 0.
- `String[] GetMessages()`. Calls `_virtualMachine.GetMessagesAndClear()`, it is *not* read-only despite sounding like that. Calling this drains the VM's message buffer as a side effect. Calling it twice in a row returns an empty array the second time.
- `Godot.Collections.Dictionary<String, Variant> GetRegisterMeasurement(RegCode regCode)`. Reads a `(BuiltInProcess?, Int32, UInt32, Single)` tuple from `_virtualMachine.GetRegisterMeasurement` and reshapes it into a dictionary:
  - "name": a formatted string, `"_<processname>.<regname>"` lowercased if BuiltInProcess owns this register, or `"?.<regname>"` if not tied to a known built-in process.
  - "index": regCode casts to a raw Int32 value.
  - "val_int" / "val_uint" / "val_float": the same measured value reinterpreted as these three numeric types, check which one is meaningful based on what kind of register was asked about.
- `Godot.Collections.Dictionary<String, Variant> GetLabelMeasurement(Int32 address, Int32 dataType)`. `dataType` is cast to the `DtCode` enum before forwarding to `_virtualMachine.GetLabelMeasurement(address, DtCode)`. Returns a dictionary with "val_int" and "val_float", again, same value as two different numerical types. Whatever one is meaningful depends on the `dataType` that was passed in.

#### Rendering output

- `ImageTexture GetFrame()`. Render pipeline: `_virtualMachine.GetFrameBuffer()` (raw bytes) => `Image` (320x240, R8/single-channel) => `ImageTexture`. Lazily created once then updated in place on every call after. Note: on the *first* and only first call, `GetFrameBuffer()` is fetched twice (once for `CreateFromData`, once immediately after for `SetData`). Keep this in note if you plan on patching `GetFrameBuffer()` rather than `GetFrame()` itself. If you desire to read or modify raw pixels before they become a texture, hook `_virtualMachine.GetFrameBuffer()` directly instead of this wrapper.

#### Input injection

- `Void SetPlayerInput(Int32 playerInput)`. Pure forwarding of `_virtualMachine.SetPlayerInput(playerInput)`.
- `Void SetMousePosition(Int32 positionX, Int32 positionY)`. Pure forwarding of `_virtualMachine.SetMousePosition(positionX, positionY)`.
- `Void SetMouseButtonInput(Int32 mouseButtonInput)`. Pure forwarding of `_virtualMachine.SetMouseButtonInput(mouseButtonInput)`.
- `Void EnqueueKeyboardInput(Int32[] keyboardInput)`. Pure forwarding of `_virtualMachine.EnqueueKeyboardInput(keyboardInput)`.
- `Void EnqueueTerminalInput(String terminalInput)`. Pure forwarding of `_virtualMachine.EnqueueTerminalInput(terminalInput)`.

### ShellLexer

Tokenizes a line of text into shell-style tokens (whitespace/tab/quote-delimited, with escape sequences inside quoted strings). Used to parse whatever is typed into the game's terminal (ties to `VirtualMachineRunner.EnqueueTerminalInput` elsewhere).

- `String Lex(String source)`. Does *not* return lexed tokens. This *resets ALL internal state* (via `Reset()`), tokenizes `source`, and returns `_errorMessage` instead (empty string "" if no error). Call `GetTokens()` separately afterward for the actual token list. Each call is independent/stateless, not cumulative across calls.
- `String[] GetTokens()`. Returns a copy of the internal token list built from the last `Lex()`.
- `Void Reset()` *(private)*. Clears tokens, error message, source, and position counters. Called automatically at the start of every `Lex()`. Not meant to be called standalone externally.
- `Void Tokenize()` *(private)*. The actual lexer loop. Splits on whitespace/tab/double-quote characters. A `"` triggers `TokenizeString()` for quoted string handling. A newline resets the line-start tracking used by `Column`.
- `String TokenizeString(Int32& nextIdxChar)` *(private)*. Parses a quoted string from the current position, handling escapes, `\\`, `\n`, `\t`, `\"`, `\0`. Sets _errorMessage to "unterminated tring" or "invalid escape sequence in string" on failure (caught later by `Lex()`'s return value).
- `Int32 get_Column()` *(private property)*. `_idxChar - _idxLineStart` is the current column within the line. Purely internal for the lexer loop. Not exposed publicly, does not feed into any error message.

### AssemblerRunner

Thin bridge wrapper around the actual `Assembler` class in `Asm.dll`. Almost every method here forwards to (or reshapes output of) an `_assembler` call. To see the actual logic, check there.

- `Void Assemble(String source, String projectPath, Dictionary<String,String> virtualFolderToPath, Int32 assemblerMode)`. Copies the Godot dictionary into a plain C# `Dictionary<string,string>`, then calls `_assembler.Assemble(...)`. `assemblerMode` crosses the bridge as `Int32` (Variant has no enum case, seen before) and gets cast back into `Assembler.AssemblerMode` internally. Check `Assembler.AssemblerMode`'s values by decompiling Asm.dll.
- `Boolean IsResultsAvailable()`. Pure forwarding of `_assembler.AreResultsAvailable`.
- `Byte[] GetProgram()`. Pure forwarding of `_assembler.Program`, a live reference, not a copy.
- `Dictionary<Int32,Int32> GetAddressToLineMap()`. Wraps `_assembler.AddressToLine` in `ToGodotDict`, a straight key/value copy, no reshaping performed.
- `Dictionary<Int32,Int32> GetLineToAddressMap()`. Builds the inverse of `AddressToLine` by hand (swaps key/value while copying), recomputed every call, not cached.
- `Int32[] GetInstructionAddresses()`. Copies `_assembler.InstructionAddressSet` (a `HashSet<Int32>`) into a plain array via a manual loop.
- `Dictionary<String,Int32> GetLabelToLineMap()`. This iterates `LabelMappingMetadata.LabelToLine` as loose `DictionaryEntry` pairs with a type-check per entry, implying that `LabelToLine` is a loosely-typed collection (e.g. `Hashtable`) internally in Asm.dll, not a generic dictionary.
- `Array<Dictionary> GetBookmarks()`. Maps each `LabelMappingMetadata.Bookmark` object into a loose `Dictionary` with `line`/`is_sub`/`title` keys.
- `Dictionary<String,Int32> GetLabelToAddress()`. Straight `ToGodotDict` copy of `LabelMappingMetadata.LabelToAddress`.
- `Dictionary<Int32,String> GetAddressToLabel()`. Straight `ToGodotDict` copy of `LabelMappingMetadata.AddressToLabel`.
- `String[] GetDiagnosticStrings()` *(static)*. Pure forwarding of `DiagnosticSink.Instance.GetDiagnosticStrings(useBBCode: true)`. BBCode is hardcoded on, returned strings carry Godot BBCode markup (e.g. `[color=...]`), not plain text.
- `Array<Dictionary> GetDiagnostics()` *(static)*. Filters `DiagnosticSink.Instance.Diagnostics` down to only `CitedDiagnostic`s that have a `AssemblySource.IsMainSource()` that is true, then flattens each into a dict (`severity`/`line`/`column`/`length`/`description`/`notes`). Diagnostics from included/imported files are silently dropped, only the main file's diagnostics come out of this.
- `Dictionary LookupSymbolAtLineColumn(Int32 line, Int32 column)`. Wraps `_assembler.LookupSymbolAtLineColumn(...)`, flattening the returned `SymbolData` into `type`/`name`/`meta_code`/`line`/`start_col`/`end_col` keys. `type` is an enum cast to `Int32`.
- `Dictionary GetLabelData(String labelName)`. Wraps `_assembler.GetLabelData(labelName)` (tuple return), deconstructed into `name`/`address`/`usage`/`data_type`/`array_item_count` keys. `usage` and `data_type` are enums cast to `Int32`.
- `String TabsToSpaces(String source)` *(static)*. Pure forwarding of `Assembler.TabsToSpaces(source)`. See Asm.dll for more information.

### TextSearcher

Literal text search/replace over a string, with line/column to flat-index conversion and wraparound search. Despite being built on Regex internally, every search pattern is `Regex.Escape`'d first, so everything passed in is treated as literal text to find, not a real regex, no matter what `MatchMode` is picked.

- `Void SetText(String newText)`. Sets `_text` (null becomes `""`), recomputes `_lineStarts` via `ComputeLineStarts()`, and resets search state. Clears `_regexPattern` and `_matches`. Call this whenever the underlying text changes, nothing else does it.
- `Void SetPattern(String pattern, MatchMode mode)`. Clears `_matches`, builds a regex from `pattern`/`mode` via `BuildRegex` and runs it against `_text`, storing every match's start/length. If `_text` or `pattern` is null/blank, it returns early right after clearing `_matches`, `_regexPattern` is not reset in that case, so a stale regex from an earlier call can survive and still get used by `Replace()` even though `GetMatchCount()` now reads 0.
- `Int32 GetMatchCount()`. Pure forwarding of `_matches.Count`.
- `Dictionary<String,Variant> Search(Int32 line, Int32 column, Boolean searchForward)`. Converts `line`/`column` into a flat-index, binary-searches `_matches` for the next one at/after that index, and wraps around circularly (`searchForward` false wraps to the last match, true wraps to the first). Returns an empty dictionary if there are no matches at all. Doesn't mutate any state or move a cursor itself, it just hands back `line`/`column`/`length`/`index` for the caller to act on.
- `String Replace(String replacement)`. This is a global replace, not a match replace, it runs `_regexPattern.Replace(_text, replacement)` across the whole text, swapping every match at once. Returns the modified string, does not mutate `_text` or recompute `_matches`/`_lineStarts`. If you want to keep searching the result, you need to feed it back through `SetText()` yourself. Returns `_text` unchanged if text/replacement/pattern is null/blank.
- `Regex BuildRegex(String pattern, MatchMode mode)` *(private, static)*. Builds the regex per mode. All four escape `pattern` first (see note above). Throws `ArgumentOutOfRangeException` for any other mode value: 
  - `Basic` = case-insensitive literal.
  - `Case` = case-sensitive literal.
  - `Word` = case-insensitive with `\b` word boundaries. 
  - `CaseWord` = case-sensitive with `\b` word boundaries
- `Void ComputeLineStarts()` *(private)*. Builds `_lineStarts`. This is the flat-string index where each line begins (0, then one past every `\n`). Used for the line/column to-and-from index conversions below.
- `Int32 LineColumnToIndex(Int32 line, Int32 column)` *(private)*. `_lineStarts[line] + column`, no bounds checking, an out-of-range `line` throws.
- `Void IndexToLineColumn(Int32 index, Int32& line, Int32& column)` *(private)*. Binary searches `_lineStarts` for which line `index` falls in, then computes `column` as the offset from that line's start.
- `Int32 FindNextMatch(Int32 caretIndex)` *(private)*. Binary search over `_matches` (sorted by start index, since `Regex.Matches` returns matches in order) for the first one at or after `caretIndex`. Returns `_matches.Count` if nothing qualifies. `Search()` is what turns that into a wraparound.

### CsUtils

Static string/fuzzy-match/filesystem helpers, no instance state. Several of these are "safe" wrappers that catch exceptions, meaning a caller gets zero information when something actually goes wrong inside them.

- `String StringReplace(String source, String searchTerm, String replacement)` *(static)*. Despite the name, this is not a literal substring replace, this builds a `\b<escaped searchTerm>\b` word boundary regex, so `"cat"` won't match inside `"category"`. This is wrapped in a catch-all try/catch that silently returns `source` unchanged on any failure.
- `String StringBulkReplace(String source, Dictionary<String,String> pairs)` *(static)*. Same word boundary regex approach as above, but every key is joined into one pattern and replaced in a single regex pass, not sequentially chained replaces, so one replacement's output can never accidentally get re-matched as another key. This has the same silent catch all and return `source` behavior as above.
- `String[] GetMostSimilarTerms(String target, String[] candidates, Int32 maxCount, Int32 maxGap)` *(static)*. Fuzzy matches `candidates` against `target` by Levenshtein distance, used for the "did you mean [word]?" suggestions. The distance cutoff (`maxDistance`) is computed automatically from `target.Length` (`ceil(len/2)+1`), not something you pass in. `maxCount` is a ceiling, not a target. Results also stop early the moment the gap between two consecutive candidates' distances exceeds `maxGap`, so you can get fewer than `maxCount` results even with more candidates available. Catch all try/catch returns an empty array on failure.
- `String StripTrailingWhitespace(String text)` *(static)*. Strips trailing spaces/tabs at the end of each line (not leading whitespace, not blank-line collapsing), processed by manual index scanning rather than `Split`/`Join`. Always normalizes the result to end in exactly one trailing `\n`, even if the input had none or several. This has no try/catch, unlike many here, an exception propagates to the caller.
- `Boolean TryDeleteDirectory(String path)` *(static)*. Recursive delete (`Directory.Delete(path, true)`), returns `true`/`false` for success. Same as the other `Try*` style wrappers here, any failure reason (permission denied, not found, in use) is swallowed, you only ever get a bare `false`.
- `Boolean IsAsciiString(String text)` *(static)*. Checks against a generated regex matching printable ASCII plus tab/newline/NUL. An empty string passes (`text.Length != 0`, breaks before the regex even runs), if you need to reject empty input specifically, check that yourself first.
- `Regex RegexMscii()` *(private, static)*. The .NET 8 source-generated regex used by `IsAsciiString`. Does not have any logic on its own, it just returns the generator's cached instance.


