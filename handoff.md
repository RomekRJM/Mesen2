# Handoff: Watch "Description" column + automatic C function variables

## What was added

The debugger's **Watch** list (debugger window tab and the standalone Watch window) now has:

1. A third column, **Description**, which explains what a memory location means in the C code.
2. An **automatic section** under the user's own entries. When execution is paused inside a C function (breakpoint or step), it lists the variables that function uses, with addresses resolved from the cc65 `.dbg` file.

Example: paused in `check_for_combo_p1()` of `GopnikTournament/combo/combo.c`:

| Name | Value | Description |
|---|---|---|
| `[_tmp1], U1` | 0 | `currentComboIndex` |
| `[_tmp2], U1` | … | `expectedCurrentState` |
| `[_p1Combo+3], U1` | 10 | `p1Combo.framesLeftToCancel` |
| `[_oppositeStatus+1], U1` | … | `oppositeStatus.status` |
| `[_k], U1` | … | `k` |
| `[_dudeComboStartIndices, 2]` | … | `dudeComboStartIndices` |

Which entries are generated:
- **File aliases**: every object-like `#define name expr` in the function's `.c` file that is visible at the function and expands to a variable or struct field, e.g. `#define currentComboIndex tmp1`. These are listed even when the function does not use them.
- **Variables used in the function body**, in order of first use, without duplicates.

Formatting rules:

| C usage | Watch expression |
|---|---|
| 1-byte scalar | `[_var], U1` (`S1` for `signed char`) |
| 2-byte int | `{_var}, U2` / `S2` |
| pointer | `{_var}, H2` |
| struct field (scalar) | `[_var+offset], U1` |
| array, struct used as a whole, array/struct field | `[_var+offset, size]` (whole array) |
| `auto` local / parameter (cc65 C stack) | `[{c_sp}+offs], U1`, description suffixed with `(C stack)` |

Behaviour:
- Auto entries are shown in italic grey. They are **not saved** in the workspace or watch file.
- Editing an auto entry's Name turns it into a regular (saved) watch entry. Leaving the textbox without changes keeps it as an auto entry.
- Auto entries that match a user entry (same expression, ignoring whitespace and case) are not duplicated.
- User entries also get a description when their address can be computed (`[label]`, `[label+n]`, `[$addr]`, `{…}`, `[label+n, len]`). Example: `[_tmp1]` → `currentComboIndex (tmp1)`, `[_player1Status+10]` → `player1Status.name[1]`.
- The current function is only looked up while emulation is **paused**. While the game runs, the last function's entries stay and only their values refresh, so the list doesn't flicker.
- Toggle: Watch list context menu → **Show Current C Function Variables** (`DebuggerConfig.WatchShowCFunctionVariables`, default on).
- "View in Memory Viewer" / "Go to Location" work on auto entries (they use the resolved address).
- The array syntax now accepts an offset: `[label+3, 10]` and `[$300+3, 10]`.

## Why the C sources must be parsed

The cc65 `.dbg` file has no usable C type info: every `csym` has `type=27`, and `#define`s are not exported at all. So struct layouts, array sizes and aliases are read from the C sources. The `.dbg` file supplies:
- function scopes and their PRG ranges (`scope ... type=scope, span=`),
- module ↔ file mapping (`file ... mod=`),
- C symbols per scope (`csym ... scope=, sc=ext|static|auto, offs=, sym=`),
- the asm symbol addresses (`sym ... type=imp, exp=` → `type=lab, val=`).

## Files

| File | Change |
|---|---|
| `UI/Debugger/Integration/CSourceAnalyzer.cs` | **New.** Lightweight cc65 C parser plus function analysis. Public types: `CVariableLocation`, `CFunctionScopeInfo`, `CWatchEntry`, `CFunctionWatchInfo` (with `Describe(addr, len)`). |
| `UI/Debugger/Integration/DbgImporter.cs` | Parses `mod=` on files, extra `csym` fields (`scope`, `sc`, `offs`), and all scopes, including unnamed module scopes, into `_cScopes`. New public `CpuType` and `GetCFunctionWatchInfo(AddressInfo absPrgAddress)`. Private helpers `GetCFunctionScope`, `GetCFunctionSourcePath`, `ResolveCVariable`, `GetVariableLocation`, `GetStackPointerLabel`, `GetLabelSymbol`. `ResolveSourcePath` was extracted from `LoadFileData` (no behaviour change). |
| `UI/Debugger/WatchManager.cs` | Evaluation extracted to `EvaluateEntry`. Auto section added in `GetWatchContent`. New `UpdateFunctionInfo`, `DescribeExpression`, offset support in array syntax. `WatchValueInfo` gets `Description`, `IsAuto`, `AutoExpression`, `RelAddress`. |
| `UI/Debugger/ViewModels/WatchListViewModel.cs` | Copies the new fields. Editing an unchanged auto entry is a no-op. Delete/Move only apply to user entries. New context-menu toggle. `GetLocation` uses `RelAddress` for auto entries. |
| `UI/Debugger/Views/WatchListView.axaml` | Description column and `TextBox.auto` style. |
| `UI/Config/Debugger/DebuggerConfig.cs` | `WatchShowCFunctionVariables`. |
| `UI/Debugger/Utilities/ContextMenuAction.cs` | `ActionType.WatchShowCFunctionVariables`. |
| `UI/Localization/resources.en.xml` | `colDescription` and the toggle's label (the UI only ships English resources). |

## Data flow

```
WatchListViewModel.UpdateWatch()
 └ WatchManager.GetWatchContent()
    ├ UpdateFunctionInfo()  (only when EmuApi.IsPaused())
    │   PC → DebugApi.GetAbsoluteAddress → DbgImporter.GetCFunctionWatchInfo(abs)
    │     ├ GetCFunctionScope: smallest C function scope whose PRG range contains PC
    │     ├ GetCFunctionSourcePath: the .c file (C line at PC, or the module's .c file)
    │     └ CSourceAnalyzer.GetFunctionInfo(scope, name => ResolveCVariable(scopeId, name))
    │         ├ TranslationUnit (cached per .c path): preprocess (#include "…", #define,
    │         │   #if/#ifdef/#elif/#else, #undef, #pragma once) → token stream with macro-alias
    │         │   marks → DeclarationParser: typedefs, struct/union layouts, globals, functions
    │         └ FunctionAnalysis (cached per scope id): params/locals, file aliases,
    │             body accesses (var / var.f.f / var[..] / ptr->..) → CWatchEntry list
    ├ user entries: EvaluateEntry + DescribeExpression → CFunctionWatchInfo.Describe
    └ auto entries: EvaluateEntry + description from the analysis
```

Variable resolution (`ResolveCVariable`): look in the function's `csym` scope, then its parent scopes (the module scope has the `extern`s the module references). `sc=auto` resolves to a stack location (`c_sp` or `sp` label + `offs`). Otherwise `sym` → follow `exp` to the label. If the variable is not referenced by the module (e.g. an alias for `tmp4` that no function uses), fall back to a global `_name` label. The watch expression uses the Mesen label name when a label exists at that exact address, otherwise `$XXXX`.

## Known limitations

- The preprocessor is simplified: function-like macros are not expanded (their arguments are still analysed), `#include <…>` is ignored, and `-D` command line defines are unknown (treated as undefined/0, like cpp without them). `#if` uses real C semantics (unknown identifiers = 0).
- Pointer dereferences (`ptr->x`, `*ptr`, `ptr[i]`) show the pointer itself (`{_ptr}, H2`), not the pointed-to data.
- Array element accesses show the whole array (as requested), not the indexed element.
- `auto` locals/parameters are approximate. `offs` is relative to `c_sp` after the function prologue, so values are wrong before the prologue has run, or while arguments for a call are being pushed. Arrays/structs on the C stack are skipped (the watch syntax cannot express them). Register variables (`sc=reg`) are not handled.
- Analysis results are cached per loaded `.dbg` file. If you edit the C sources without rebuilding/reloading the `.dbg`, reload it (or restart the debugger) to refresh.
- Struct layout assumes cc65 rules: no padding, `int`/pointers = 2 bytes, `long` = 4, `enum` = 2. Bitfields are approximated by their base type.
- Only `DbgImporter` (cc65 `.dbg`) is supported. Other symbol providers just get an empty Description.

## Fork state / upstream sync (2026-10-19)

- Remotes: `origin` = https://github.com/RomekRJM/Mesen2 (the fork), `upstream` = https://github.com/SourMesen/Mesen2 (official).
- `master` was merged with `upstream/master` (182 upstream commits, no conflicts) in merge commit `d2d7874d`. Upstream did not touch any file of the Watch feature. `resources.en.xml` auto-merged; its second `colDescription` belongs to the Cheats form, so it is not a duplicate.
- Backup of the pre-merge state: branch `backup/pre-upstream-merge` (= `08b62407`). It can be deleted once the merged build is confirmed working.
- Nothing has been pushed yet: `git push origin master`.
- Future syncs: `git fetch upstream && git merge upstream/master`. The fork's own changes are UI-only (C#), with nothing in Core. Expect conflicts only in `WatchManager.cs`, `DbgImporter.cs`, `WatchListViewModel.cs`, `WatchListView.axaml`, `resources.en.xml`, and the profiler files from earlier fork work.

## Build / test

- **Full build (needed after any upstream sync, because upstream changes the native interop structs):**
  `"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" Mesen.sln /m /restore /p:Configuration=Release /p:Platform=x64`
  The output goes to `bin\win-x64\Release\Mesen.exe`. `MesenCore.dll` is packed into `Dependencies.zip`, and Mesen extracts it over the copy in the Mesen home folder (`OneDrive\Dokumenty\Mesen2`) at startup. Verified: the build succeeds after the merge, and the zip contains `MesenCore.dll`.
- **UI-only rebuild** (C# changes, core unchanged): `dotnet build UI/UI.csproj -c Release -p:Platform=x64 -p:SolutionDir=D:\workspace\Mesen2`. Without `SolutionDir`, the output goes to `<cwd>\..\bin`. Use the same configuration as the last full build, or the core in `Dependencies.zip` will be missing or stale. `bin\win-x64\Debug` currently has the new UI but **no** matching core, so use Release.
- The analyzer was tested on its own with a scratch console app that compiles `CSourceAnalyzer.cs` and uses a fake resolver built from `gopnik.dbg` labels. For `check_for_combo_p1`, `check_combo_index_p1` and `load_combo_soa`, the output matched the expected table (aliases tmp1..tmp6, `p1Combo` fields, `oppositeStatus.hp/status`, and `tmpArr1` correctly omitted because its use is inside `#if SHOW_PLAYER_COMBO` = 0).
- **Not yet verified in the running emulator GUI** (neither before nor after the upstream merge). Manual test with `bin\win-x64\Release\Mesen.exe`: load `GopnikTournament/gopnik.nes` (the `.dbg` auto-loads), put a breakpoint in `check_for_combo_p1` (Source View, `combo/combo.c`), trigger it and check the Watch tab. Values should match the Memory Viewer (`_tmp1` = $41, `_p1Combo` = $65B0).

## Ideas for next steps

- Expand function-like macros.
- Show the indexed element when the index is a constant (`tmpArr1[14]`), as an extra entry next to the whole array.
- Optionally include variables of functions called from the current one (same file).
- Follow pointers whose target type is known (`{ptr}` + field offset, using Mesen's nested `[{_ptr}+n]` syntax).
- Invalidate the analysis cache when source file timestamps change.
