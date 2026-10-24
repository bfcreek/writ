# Plan: PowerShell equivalency with intrinsic tool support

Target: an `.writ` file compiles to a runnable `.ps1` (PowerShell 7). Anything a person would write a PowerShell script for can be written as constrained English, checked before it runs, and run with `pwsh`. Tools, including MCP servers, are commands in that script, not a separate mode.

## 0. Status

P0–P6 are implemented (72 tests, every sample checks on both targets). What landed differently from the text below:

- Lockfile verification (`E4008` on tool drift, `--frozen` → `E4009`) arrived with P3 rather than P6; P6 added `explain` and `diff`.
- `Also use` merges helper declarations into the one script (functions carry `SourceFile`); a separate `.psm1` per helper is still open.
- `Group-Object` (`grouped by`), `stop with "…"`, and the `powershell-module` / `http` providers are declared in catalogs or manifests but not routed yet.
- The MCP test server is `tools/mcp-test-server.ps1` (pwsh, no Node dependency). Compile-time discovery lives in `McpDiscovery`, which has no `tools/call`.

## 1. Where we are

Done (Milestones A–C of the proposal, plus two additions):

- English source → AST → typed IR (schema 0.3.0) → validation → C# → in-process Roslyn run.
- Records, functions, assignment, if/otherwise, for each, return, arithmetic, comparison, lists, built-ins.
- Bounded model readings: omitted field types (`price and quantity`), implied operations (`subtotal`), and ordered command lists (`retry the payment, but first list the failures`). The model may only return ids from a catalog the compiler hands it. Results outside the catalog are rejected.
- Capabilities from JSON manifests: `Use payments from "…"`, `ask payments to list failed with the day`. Discover at check, invoke only at run. Unknown operation (E4002), missing effect (E4003), invalid schema (E4001).
- `IrSequence`: operations in execution order. `IrCapabilityCall`: a bound tool call with effects and a binding id.

Not done: PowerShell output, pipelines, cmdlets, dynamic objects, error handling, a live MCP adapter, lockfiles, multiple files.

Machine: pwsh 7.6 and Windows PowerShell 5.1 are installed. The plan targets pwsh 7 only.

## 2. What "PowerShell equivalency" means here

Not the whole PowerShell grammar. Equivalency means a working PowerShell user can express their everyday scripts in Writ and get a `.ps1` they would recognise. The checklist in §6 is the definition. Each row has a milestone.

Rules that do not change:

- Ordinary sentences compile deterministically. The model is consulted only for what the sentence leaves out, and only to choose from a bounded list.
- Checking a program discovers contracts. It never runs a cmdlet or a tool.
- Writes need granted effects. A tool's description never overrides that.
- The IR is the contract between the English and any target. PowerShell is a second backend over the same IR, then the default.

## 3. Architecture changes

### 3.1 Targets

`writ compile file.writ --target powershell|dotnet`. Both backends consume the same IR. The C# backend stays until the parity suite (§4, P0) passes, then PowerShell becomes the default and C# remains as a test oracle.

`writ run file.writ --entry Name --input args.json` runs through `pwsh -NoProfile -File out\file.ps1` and passes `args.json` as parameters. Exit code and output come back from pwsh.

### 3.2 Runtime module

`Writ.psm1` ships next to the generated script. It holds what `Builtins.cs` holds today (Len, Round, Sum, Append, conversions, SumMap, Filter), plus `Invoke-WritCapability` (the PowerShell counterpart of `CapabilityRuntime.Call`), and later the MCP client. Generated scripts `Import-Module` it by relative path.

### 3.3 IR additions

- `IrPipeline(Stages)`: ordered stages, each a command or a projection. Replaces the `Func` wrapper used for `IrSequence` when the steps feed each other.
- `IrCommand(Name, Parameters, Effects, OutputType, Binding)`: a cmdlet or tool call. `IrCapabilityCall` becomes one kind of command with a provider.
- `IrTry(Body, OnFailure, Finally)`.
- `IrDynamicType` and `IrValidate(Value, TargetType)`: PowerShell output without a schema is dynamic. Typed access requires a validation step (`treat the output as a list of Invoice`). Field access on dynamic without validation is an error.
- `IrWhile`, `IrSwitch` when the English forms land (P1).

Schema version bumps with each addition.

### 3.4 Catalogs the model may choose from

The catalog is the whole set of ids a model reading may return. It grows by milestone:

1. Types: integer, decimal, string, boolean, records in scope (today).
2. Language operations: sumProjection, filter (today); sort, group, first/last/count, select properties, where (P1).
3. Cmdlets: a pinned catalog with parameter names, parameter types, output type where known, and effect class (P1).
4. Tool operations: capability manifest operations (today), MCP tools (P3).

Each catalog entry carries its arguments so the model binds symbols in scope, as `payments.retry` binds `paymentId` today.

## 4. Milestones

### P0. PowerShell backend for the existing language

- `PowerShellGenerator` over the current IR: records → `class` declarations; functions → `function Name { [CmdletBinding()] param([string]$day, …) … }`; integer → `[long]`, decimal → `[decimal]`, string → `[string]`, boolean → `[bool]`, list → `[System.Collections.Generic.List[T]]`; `if/elseif/else`, `foreach`, `return`.
- `Writ.psm1` with the built-ins and `Invoke-WritCapability` reading the same mock manifests.
- `PowerShellExecutor`: writes the script to a temp folder, invokes pwsh, binds JSON input to parameters by name, parses the last JSON line of output as the result.
- Parity suite: every sample runs under both backends and produces the same value. Capability samples count calls the same way.

Acceptance: `writ run samples/calculate-total.writ --target powershell` prints 27.54, `recover-failures` prints 1, `but-first` emits `list_failed` before `retry`, and all existing tests pass under both targets.

### P1. Commands, pipelines, and the cmdlet catalog

English forms:

```text
the files are the items in the folder
the recent are the files where the last write time is after yesterday
the biggest are the recent sorted by length, largest first
the top are the first 10 of the biggest
for each file in the top
    say the name of the file and the length of the file
```

- Parser: `sorted by … (largest|smallest) first`, `the first N of`, `the count of`, `grouped by`, `the names of the items` (select), `say` (Write-Output), `while`, `until`, `otherwise when` (switch).
- Cmdlet catalog: generated once by `writ tools refresh` from `Get-Command` and `Get-Help` metadata into `tools/powershell.catalog.json`, with a hash. Entries hold verb, noun, parameter sets, output type when `OutputType` is declared, and effect class derived from the verb (Get/Test/Measure → read; New/Set/Remove/Start/Stop/Invoke → write; Invoke-WebRequest/Invoke-RestMethod → network). Only catalog entries can be bound. Checking a program reads the catalog. It does not run `Get-Command`.
- A sentence that names no known operation goes to the model with the cmdlet catalog restricted to granted effects. The reading comes back as an ordered command list (as today), lowered to `IrPipeline`.
- Codegen: pipelines become `|` chains; language operations become `Where-Object`, `Sort-Object`, `Select-Object -First`, `Group-Object`, `Measure-Object`, or the runtime module where types must be kept exact.

Acceptance: the folder sample above compiles to a readable `.ps1` and runs; a sentence that implies `Remove-Item` fails with E4003 unless the program grants `filesystem-write`.

### P2. Objects, dynamic data, and validation

- `IrDynamicType` for cmdlet output without a declared type and for JSON/CSV/REST results.
- English: `treat the response as a list of Invoice`, `read the file as JSON`, `read the file as CSV with Name and Amount`. Lowered to `IrValidate`, generated as `ConvertFrom-Json` plus a runtime shape check that fails with a clear message.
- Field access on dynamic without validation: E2008-style error pointing at the sentence.
- Hashtables and `[pscustomobject]` as the target form for records when a cmdlet needs them (`Select-Object` property lists, `ConvertTo-Json`).
- String interpolation form: `say "Processed {the count} items"`.

Acceptance: a REST sample calls `Invoke-RestMethod` (network effect granted), validates the response as a record list, and sums a field; removing the validation sentence makes the program fail to check.

### P3. Intrinsic tools: MCP as a command provider

This is Milestone D of the proposal, delivered on PowerShell.

- Manifest `provider` field: `mock` (today), `powershell-module`, `mcp`, `http`.
- `Use payments from mcp "payments-service"`: discovery at check via `tools/list` over stdio, normalised into the same `LoadedCapability` shape. Tool descriptions are data; they never enter the model request as instructions. Results without a schema are dynamic (P2).
- Lock: `file.writ.lock.json` pins manifest hashes. A changed tool schema fails the check until the lock is refreshed.
- Runtime: `Invoke-WritCapability` in the module routes by provider. The MCP client is a small .NET assembly loaded by the module (JSON-RPC over stdio), or a pure PowerShell client if it stays small. Credentials come from environment or SecretManagement, never from the manifest.
- `ask` and model-ordered command lists become `IrCommand` stages, so a tool call can sit inside a pipeline: `the failed are ask payments to list failed with the day, sorted by amount`.
- `writ tools list` and `writ tools inspect payments.retry` print the bound contract.

Acceptance: a test MCP server in the repo is discovered at check, nothing is invoked at check (the server counts calls), and `writ run` invokes it in the order the sentence meant.

### P4. Errors, results, and explicit retry

- English: `try to retry the payment, and if that fails, note the error and continue`; `if that fails, stop with "…"`; `finally, close the connection`.
- `IrTry` → `try/catch/finally`. Capability failures become `ErrorRecord` objects with the operation, binding, and arguments.
- `retry up to 3 times` only for operations the manifest marks idempotent; otherwise E4006.
- `-WhatIf` and `-Confirm`: `writ run --dry-run` passes `-WhatIf` to every write-effect command and prints the planned calls. Mock providers already support this; live providers log and skip.

Acceptance: the payments sample survives a mocked `retry` failure, reports it, and continues; a retry sentence on a non-idempotent op is a check error.

### P5. Script ergonomics and multiple files

- Entry point selection: a function named in `--entry`, or a top-level `When run, …` block that becomes the script `param()` and body. `pwsh ./recover.ps1 -Day 2026-10-20` works without the CLI.
- Comment-based help generated from the English signature, so `Get-Help ./recover.ps1` describes the parameters.
- `Also use "helpers.writ"` for multiple files → a generated `.psm1` with exported functions.
- Environment and paths: `the home folder`, `the environment variable PATH`, `the current folder`.

Acceptance: a two-file program compiles to a module plus a script; the script runs from a plain pwsh prompt with named parameters.

### P6. Reproducibility and explanation

- Semantic lockfile: source hash, compiler version, IR schema, every model reading (text, catalog hash, accepted selections, order), cmdlet catalog hash, tool manifest hashes.
- Frozen build: `writ compile --frozen` reuses recorded readings and refuses to call a model. Stale source or changed hashes fail.
- `writ explain file.writ` prints, per line, the chosen operations in execution order, their effects, and whether they were deterministic or a model reading.
- `writ diff` compares IR between two versions of the source.

Acceptance: CI compiles every sample with `--frozen` and no model configured.

## 5. The model's role across the plan

- Field types without a stated type (today).
- Implied operations and their order (today). Order comes from the reading, not from the sentence position. `but first`, `before`, `after`, `then`, and spelling foibles are the model's job.
- Cmdlet choice from the catalog (P1). The request includes the catalog restricted to granted effects, the symbols in scope, and any rules the program states.
- Parameter binding: the model names which in-scope symbol fills which parameter; the compiler checks the type.
- Never: inventing a cmdlet, a field, a tool, a type, or a permission. Never deciding retries or effects.

A real model (`ILanguageModel` over HTTP) replaces `InnateLanguageModel` at P1, with the stand-in kept for offline tests. The acceptance filter (`ModelReadings.Accept`) does not change.

## 6. Equivalency checklist

| PowerShell | English form | Milestone |
|---|---|---|
| Variables, arithmetic, comparison | `the tax is the subtotal times the rate` | done |
| `if/elseif/else` | `if … otherwise if … otherwise` | done |
| `foreach` | `for each item in the items` | done |
| Functions with typed params | `To Name, given … returning …` | done |
| Arrays and lists | `a list containing 1, 2 and 3`, `with x added` | done |
| Tool calls with permissions | `ask payments to retry with the id` | done |
| Pipelines `|` | sentence with ordered steps, `sorted by`, `the first N of` | P1 |
| `Where-Object`, `Sort-Object`, `Select-Object`, `Group-Object`, `Measure-Object` | `where`, `sorted by`, `the names of`, `grouped by`, `the count of` | P1 |
| Cmdlets (`Get-ChildItem`, `Get-Process`, …) | `the files in the folder`, `the running processes` | P1 |
| `while`, `do/until`, `switch` | `while`, `until`, `otherwise when` | P1 |
| `Write-Output`, `Write-Host` | `say …` | P1 |
| String interpolation | `"Processed {the count} items"` | P2 |
| Hashtables, `[pscustomobject]` | records | P2 |
| JSON, CSV, REST | `read … as JSON`, `treat … as a list of X`, `Invoke-RestMethod` via catalog | P2 |
| Dynamic objects | `IrDynamicType` with required validation | P2 |
| MCP tools | `Use payments from mcp "…"` | P3 |
| Modules as tools | `Use az from module "Az.Accounts"` | P3 |
| `try/catch/finally` | `try to …, and if that fails …, finally …` | P4 |
| `-WhatIf`, `-Confirm` | `writ run --dry-run` | P4 |
| `throw`, `Write-Error` | `stop with "…"`, `note the error` | P4 |
| Script `param()` and help | `When run, given …` | P5 |
| Modules from several files | `Also use "helpers.writ"` | P5 |
| `$env:`, paths | `the environment variable X`, `the home folder` | P5 |
| `ForEach-Object -Parallel`, jobs | `for each item at the same time` | after P6 |
| Classes with methods, .NET interop | not planned; records only | — |
| Arbitrary expressions, splatting, providers (`HKLM:`) | not planned | — |

## 7. Decisions to take now

1. pwsh 7 only. No Windows PowerShell 5.1 output.
2. The C# backend stays as the parity oracle through P2, then is kept only if it is still cheap.
3. Lists are `List[T]`, not arrays, so `Append` and typing stay exact; conversion to arrays happens at pipeline boundaries.
4. The cmdlet catalog is pinned in the repo and refreshed deliberately. Discovery from the live machine is a command the developer runs, not something the compiler does.
5. Dynamic data is a type, and typed access requires a validation sentence. No guessing fields from sample output.
6. Model readings are recorded in the lockfile from P1 on, so P6 is a change in enforcement, not a migration.

## 8. First step

P0, in this order:

1. `src/Writ.Compiler/Codegen/PowerShellGenerator.cs` over the current IR, including `IrSequence` and `IrCapabilityCall`.
2. `src/Writ.Runtime.PowerShell/Writ.psm1` with the built-ins and `Invoke-WritCapability`.
3. `src/Writ.Compiler/Execution/PowerShellExecutor.cs` and `--target` on `compile` and `run`.
4. `tests/Writ.Tests/PowerShellParityTests.cs`: every sample, both targets, same result.
5. `docs/SUPPORTED.md`: a PowerShell column.
