# Writ — Supported Subset (PowerShell target, P0–P6)

Research prototype. Source text reads as constrained English. The compiler translates those sentences into typed declarations and operations, then writes a runnable PowerShell 7 script. It does not treat a file as a free-form prompt.

## Commands

```text
writ check <file.writ> [--frozen]
writ compile <file.writ> [--out dir] [--target auto|powershell] [--frozen]
writ run <file.writ> --entry Name [--input args.json] [--target auto|powershell] [--dry-run]
writ lock <file.writ>
writ tools list <file.writ>
writ tools inspect <file.writ> <alias.operation | pwsh.Cmdlet-Name>
writ explain <file.writ>
writ diff <file.writ>
```

`writ` here means `dotnet run --project src/Writ.Cli --`. With `--target auto` (the default), compilation accepts the PowerShell script when the constraint files allow it. `powershell` forces that script. `compile` writes `<name>.ps1`, `Writ.psm1`, `<name>.ir.json`, and `<name>.writ.lock.json`.

## How a program reads

```text
# Show the largest files in a folder.
Allow reading files
Also use "folder-helpers.writ"

When run, given a folder which is a string, returning a list of string
    the files are the items in the folder, files only
    the biggest are the first 3 of the files sorted by length, largest first
    the lines are an empty list
    for each file in the biggest
        the size is the length of the file as an integer
        the line is Describe with the name of the file and the size
        the lines are the lines with the line added
    say "Home is {the home folder}"
    return the lines
```

The compiled script has comment-based help from the leading comments, a `param([string]$folder)` block, and runs as `pwsh ./largest-files.ps1 -folder C:\x`. Multi-word names become camelCase in the typed program and in JSON input (`a tax rate` is `taxRate`).

## What the compiler accepts

| English form | Becomes |
|--------------|---------|
| `An Name has` / `a field which is a type` | Record declaration |
| `An InvoiceItem has price and quantity` | Record; omitted types are chosen from the bounded type list |
| `To Name, given ... which is/are ..., returning a type` | Function |
| The sentences in the file | What the script does when it runs. No parameters, and it returns nothing |
| `When run, given ...[, returning a type]` | That same entry, when the script takes parameters or returns a value. One sentence may follow on the same line |
| `Also use "helpers.writ"` | Another file's types, functions, tools, and grants join this program |
| `run "greet.ps1" with name set to "Ada"` | Call a PowerShell script. Its `param()` block and `.OUTPUTS` are the contract. Needs `Allow running scripts` |
| `run "double.writ" with the value` | Call another Writ script's `When run` block. Checked, not run, until this program runs |
| `Allow reading files, writing files, network access, changing processes` | Effect grants for commands and bare MCP tools |
| `Use payments from "payments.capability.json"` | Bind a capability manifest (mock or MCP-backed) |
| `Use payments from mcp "pwsh -File server.ps1"` | Bind an MCP server directly; its tool list is the manifest |
| `the name is <value>` | Assignment |
| `if <condition>` / `otherwise` / `otherwise if` / `otherwise when` | Branch |
| `for each name in <collection>` | Loop |
| `while <condition>` / `until <condition>` | Loop |
| `try to` / `if that fails` / `finally` | try / catch / finally; `the error` is the message in the failure block |
| `return <value>` / `give back <value>` | Return |
| `say <value>` / `display <value>` / `print <value>` | Console output. `and` joins several values. `{…}` interpolates any expression |
| `clear`/`wipe`/`erase`/`blank` the `screen`/`console`/`window`/`display`/`terminal`/`host`, or `cls` | Clear-Host |
| `wait`/`pause`/`sleep`/`hold`/`delay` [for] 2 `seconds`/`minutes`/`hours`/`milliseconds`, or `pause for the delay` | Start-Sleep. Minutes and hours become seconds. `ms` is milliseconds |
| `stop`/`halt`/`fail`/`abort`/`quit`/`exit`/`die`/`end` with "failed" | `throw`. Stops the script |
| `warn`/`caution`/`alert` "missing", or `warn that the folder is missing` | Write-Warning. A bare name passes that value; otherwise the rest of the line is the message |
| `note`/`mention`/`verbose` "detail", or `note the error` | Write-Verbose |
| `read "Your name"` / `prompt for the question` / `ask for "Your name"` / `ask the user`/`person`/`someone`/`operator`/`human`/`reader` for "Your name" | Read-Host. `ask <capability> to …` is still a capability call |
| `go`/`change`/`switch`/`cd`/`head`/`move` to the folder, or `enter the folder` | Set-Location. The script's own location; no `Allow` |
| `add`/`append`/`tack` the line `to`/`onto`/`into` the file | Add-Content. Needs `Allow writing files` |
| `write`/`save` the text `to`/`into` the file, or `overwrite the file with the text` | Set-Content. Needs `Allow writing files` |
| `copy`/`duplicate` the file to the destination | Copy-Item. Needs `Allow writing files` |
| `move`/`relocate` the file to the destination | Move-Item. `move to the folder` is Set-Location. Needs `Allow writing files` |
| `rename`/`retitle` the file `to`/`as` the newname | Rename-Item. Needs `Allow writing files` |
| `remove`/`delete`/`erase`/`wipe` the file, optionally `and everything in it` | Remove-Item. Needs `Allow writing files` |
| `create`/`make` the `folder`/`directory`/`file` | New-Item. Needs `Allow writing files` |
| `the combined`/`joined` path of the folder and the name, `the path joining` them, `the join of` them | Join-Path |
| `the parent folder`/`directory`/`dir` of the path, or `the containing folder`/`directory` of the path | Split-Path -Parent |
| `the file name`/`filename`/`base name`/`leaf` of the path | Split-Path -Leaf |
| `the full`/`absolute`/`resolved`/`complete` path of the file | Resolve-Path. Needs `Allow reading files` |
| `start`/`launch`/`open`/`run` the `program`/`app`/`application`/`executable`/`tool` | Start-Process. Needs `Allow changing processes` |
| `start`/`launch` the service, `stop`/`kill`/`end`/`halt` the service, `restart the service` | Start-Service, Stop-Service, Restart-Service. `daemon` works as service. Needs `Allow changing processes` |
| `stop`/`kill`/`end`/`halt` the process | Stop-Process. Needs `Allow changing processes` |
| `plus`, `minus`, `times`, `divided by`, `modulo` | Arithmetic |
| `is`, `is equal to`, `is greater than`, `is less than`, `is at least`, `is at most` | Comparison |
| `and` / `or` / `not` | Boolean |
| `the price of the item` | Field access |
| `the names of the files` | Field projection over a list |
| `Classify of n` / `Helper with 1 and 2` | Call |
| `the length of`, `the count of`, `rounded to N places`, `as a string` | Built-in call |
| `the files sorted by length[, largest first \| smallest first]` | Sort |
| `the first 3 of the files` | Take |
| `an empty list` / `a list containing 1, 2 and 3` | List |
| `the results with <value> added` | Append |
| `the sum of price times quantity for each item in items` | Sum over a field product |
| `the items where the quantity is greater than 0` | Filter |
| `the text as JSON` / `the text as CSV` | Parse text into untyped data |
| `the data as a list of Invoice` / `the outcome as a RetryResult` | Check untyped data against a declared type at run time |
| `the environment variable HOME`, `the home folder`, `the current folder`, `the temp folder` | Environment |
| `ask payments to list failed with the day[, retrying up to 3 times]` | Capability call; retry only for reads (`E4006` otherwise) |
| A phrase that is not one of the forms above | Open intent, resolved by the model against the command catalog or rejected |

### Types

`an integer`, `a decimal`, `a string`, `a boolean`, `a list of T`, names introduced with `An ... has`, and *untyped data* (what a command, a JSON parse, or a bare MCP tool returns). Untyped data converts to primitives at run time and must be checked with `as a <Type>` before it is used as a record (`E2014` hints at this).

## Commands (PowerShell cmdlets)

`tools/powershell.catalog.json` is a pinned catalog of cmdlets with English phrases, parameters, and effects: `Get-ChildItem`, `Get-Item`, `Get-Content`, `Set-Content`, `Add-Content`, `New-Item`, `Copy-Item`, `Move-Item`, `Rename-Item`, `Remove-Item`, `Test-Path`, `Join-Path`, `Split-Path`, `Resolve-Path`, `Set-Location`, `Get-Process`, `Stop-Process`, `Start-Process`, `Get-Service`, `Start-Service`, `Stop-Service`, `Restart-Service`, `Get-Date`, `Invoke-RestMethod`, `Invoke-WebRequest`, `Import-Csv`, `Export-Csv`, `Clear-Host`, `Start-Sleep`, `Read-Host`. A sentence like `the items in the folder, files only` is read by the model against that catalog and becomes `Get-ChildItem -Path $folder -File`. When several phrases match, the most specific one wins. The cmdlet's effect must be granted with `Allow …` (`E4003`). Commands with write effects receive `-WhatIf` under `--dry-run`.

The sentence table's console, file, path, process, and service rows are recognized directly, including the verb and noun variants written there. A looser sentence that does not fit one of those shapes is still read from the catalog. A program that uses one of the direct sentences compiles to PowerShell.

## Model readings

When a sentence leaves a type or an operation unstated, the compiler passes that text to a model together with the closed list of types and operations this language can represent. The model returns one or more entries from that list. A return value outside the list is rejected (`E2105`). Several competing choices for the same step are rejected (`E2104`).

When the entries are operations, their order is the order they run. The model straightens human foibles before it returns that list: `but first`, `gut first`, `before`, and `after` move a step earlier, and `then` keeps spoken order.

An explicit type or definition is a specific rule and is not sent back to the model. Every accepted reading is recorded in the lockfile and shown by `writ explain`.

### Which model

With no model file, readings use the built-in stand-in. It is deterministic and offline. A project file `writ.model.json`, or `~/.writ/model.json` when the project has none, names the model the command line calls. The nearest project file wins, the same walk as `writ.constraints.json`. `--model <name>` selects a profile for that command. A library call reads the project file only.

The file is either one model or a named list:

```json
{
  "use": "ollama",
  "models": {
    "innate": { "provider": "innate" },
    "ollama": { "provider": "ollama", "model": "qwen2.5-coder" },
    "openai": { "provider": "openai", "model": "gpt-4.1-mini" },
    "copilot": { "provider": "copilot", "model": "gpt-4o" },
    "compatible": {
      "provider": "compatible",
      "model": "local-model",
      "endpoint": "http://127.0.0.1:1234/v1"
    }
  }
}
```

| Provider | Also written as | Default endpoint | API key variable |
|----------|-----------------|------------------|------------------|
| `innate` | `built-in` | — | — |
| `ollama` | | `http://127.0.0.1:11434/v1` | optional, `apiKeyEnv` |
| `openai` | | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| `copilot` | `github-copilot` | `https://api.githubcopilot.com` | `GITHUB_TOKEN` |
| `compatible` | `openai-compatible`, `custom` | required | optional, `apiKeyEnv` |

`compatible` is any server that speaks the OpenAI chat completions API. Ollama, OpenAI, and Copilot use that same request. Copilot also sends `Copilot-Integration-Id: copilot-developer-cli`. A single model can be written without `models`, as `{ "provider": "ollama", "model": "qwen2.5-coder" }`.

`apiKeyEnv` names the environment variable that holds the key. A key written in the file is `E2107`. An unknown provider, a missing model name, a missing endpoint, or an empty required variable is the same code. The model is asked only when a sentence leaves a type or an operation unstated. A transport failure or a reply that is not a reading is `E2106`. The catalog check is unchanged: an id outside the list is still `E2105`.

`temperature` defaults to 0 and may be from 0 to 2. `timeoutSeconds` defaults to 120 and may be from 1 to 600. `samples/model` shows every provider and leaves `use` on `innate`, so a check of that folder stays offline.

`--debug` on `check`, `compile`, `run`, `lock`, `explain`, `diff`, and `tools` writes each rule decision and each model call to standard error. A rule match says why the model was not asked. A model call shows the sentence, the catalog that was offered, and the reply. A chat provider also shows the request URL and the response body. The API key is not written.

## Capabilities and MCP

A manifest declares types, operations, allowed operations, granted effects, and a `provider`: `mock` (results under `mock`), or `mcp` with a `command` (stdio) or `baseUrl` (HTTP). Checking a program that needs an MCP server starts it, sends `initialize` and `tools/list`, confirms every declared operation exists, and stops. The compile-time client has no `tools/call`; invocation lives only in the PowerShell runtime (`Writ.psm1`).

`Use payments from mcp "…"` skips the manifest: tools become operations, input schemas become arguments, results are untyped data, read-only tools need `Allow network access`, and the rest need `Allow changing external systems`. The quoted text is a command line, or an `http://` or `https://` URL. `tools/mcp-test-server.ps1` is the in-repo server used by tests; it logs every JSON-RPC method so tests can prove `check` never calls a tool.

The same servers can be declared in `writ.mcp.json`. The compiler reads every such file from the program's folder upward, then `%USERPROFILE%\.writ\writ.mcp.json`. The closest file wins for a given alias; the user file is only the fallback. A duplicate alias in one file is an error. An explicit `Use` for the same alias wins over all of them. A server is queried only when the program asks for that alias, or when a sentence names the alias so the model can select one of its tools. Checking still only sends `initialize` and `tools/list`.

A server is stdio when it has `command`, and REST when it has `url`. A relative command runs in the directory of the file that declared it.

```json
{
  "servers": [
    { "alias": "payments", "command": "pwsh -NoProfile -NonInteractive -File ../../tools/mcp-test-server.ps1" },
    { "alias": "search", "url": "http://127.0.0.1:8080/mcp" }
  ]
}
```

Every generated script accepts `-ListTools`. It prints each MCP tool the script calls, one line `alias.operation (stdio|rest) endpoint`, then exits. It does not start those servers or run the program. `writ run` dot-sources the script, so the switch does not fire there.

Starting the script checks that list against the tools each server offers. It sends `tools/list` once per server and stops before the program runs if a required tool is missing. The same check runs when `writ run` starts an entry, and when a called Writ script starts. `-ListTools` does not perform it.

Capability failures are structured: PowerShell throws `WritCapabilityException` with `Alias`, `Operation`, and `Provider`; the executor reports `payments.retry (mock): …`.

## Scripts

`run "path"` calls another script. The path is relative to the file that contains the sentence. Paths are `.writ` or `.ps1`.

A Writ script is compiled while checking, which reads its `When run` parameters and result type and never runs it. The caller must grant every permission that script grants. At run time the compiled script is started with pwsh and its result comes back. A script that calls itself, directly or through another script, is `E4012`. A Writ script with no `When run` block is `E4011`.

A PowerShell script is not executed while checking. The compiler reads the top-level `param()` block and, when present, the `.OUTPUTS` section of the comment-based help. Without `.OUTPUTS` the result is untyped data. Because the body is invisible, running it needs `Allow running scripts` (`E4003`). A missing file, or a file that is neither `.writ` nor `.ps1`, is `E4010`.

`Also use` copies helpers into this program. `run` starts the other script and uses what it returns.

## Emission

Programs emit one PowerShell script:

| Id | Language | Approach | Architecture | Offers |
|----|----------|----------|--------------|--------|
| `powershell-script` | PowerShell | script | its own process | `dry-run`, `cmdlets` |

`--target auto` accepts that script when the constraints allow it. `--target powershell` forces it and skips scoring. A constraint the script cannot meet, such as `forbid: ["external-process"]`, is `E5001`.

Constraints live in JSON. The project file is `writ.constraints.json`, next to the source or in a parent folder (the nearest file wins, same walk as `writ.mcp.json`). The user file is `~/.writ/constraints.json`. The command line reads both. A library call reads the project file only, unless it asks for the user file.

```json
{
  "require": ["dry-run"],
  "prefer": ["script"],
  "avoid": ["cmdlets"],
  "language": "powershell",
  "approach": "script",
  "architecture": "external-process"
}
```

`require` and `forbid` from both files apply. A trait that is required and forbidden is `E5002`, as is an unknown trait or a key the file does not define. When both files set `language`, `approach`, or `architecture`, the project file wins. Preferences are scored: each project `prefer` or `avoid` counts 2, each user one counts 1.

`writ explain` prints the choice. The lockfile records the emission id. `--frozen` fails when that id changes (`E4009`). A lock written before emissions were recorded still matches. A lock that recorded `csharp-library` does not.

`samples/emission` prefers a script that can dry-run.

## Lockfile

`<file>.writ.lock.json` pins the source hash, compiler version, IR schema, cmdlet catalog hash, every capability's discovery hash (manifest content or MCP tool list) and operations, every model reading, and the chosen emission. `check`, `compile`, and `run` fail when a tool changed since it was locked (`E4008`); `writ lock` accepts the change. `--frozen` fails on any difference (`E4009`), including a different emission. `writ diff` says whether an edit changed meaning or only text.

## Pipeline

English source → sentences → structural AST → includes merged → typed IR (schema 0.7.0) → checks → emission chosen from constraints → PowerShell script → run.

The PowerShell language forms Writ accepts, and the ones it leaves out, are listed in [constructs.md](constructs.md).

## Fail-closed behavior

Unknown names, unknown types, wrong arguments, non-boolean conditions, missing returns, unresolved phrases, ambiguous phrases, unknown operations, invalid manifests, missing effect grants, retried writes, missing MCP tools, drifted tools, and helper files with their own entry are errors that point at the English source.

## Not yet

`powershell-module` and `http` providers (declared but not routed), splitting helpers into their own `.psm1`, `ensure`, and runtime `infer`.
