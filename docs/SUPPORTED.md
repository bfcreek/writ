# Writ — Supported Subset (C# Machine target, P0–P6)

Research prototype. Source text reads as constrained English. The compiler translates those sentences into typed declarations and operations, then writes C# that runs on the in-process Machine runtime. It does not treat a file as a free-form prompt.

## Commands

```text
writ check <file.writ> [--frozen]
writ compile <file.writ> [--out dir] [--target auto|csharp] [--frozen]
writ run <file.writ> --entry Name [--input args.json] [--target auto|csharp] [--dry-run]
writ lock <file.writ>
writ tools list <file.writ>
writ tools inspect <file.writ> <alias.operation | pwsh.Cmdlet-Name>
writ explain <file.writ>
writ diff <file.writ>
```

`writ` here means `dotnet run --project src/Writ.Cli --`. With `--target auto` (the default), compilation selects the C# Machine target. `csharp` is the only explicit target. `compile` writes `<name>.cs`, `<name>.ir.json`, and `<name>.writ.lock.json`.

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

The compiled C# has a `MachineEntry` method for `When run` and executes in process through `writ run`. Multi-word names become camelCase in the typed program and in JSON input (`a tax rate` is `taxRate`). A keyword is special in the sentence position that uses it, and a name wherever a name is read: `To Count`, `the length is 3`, `given a which which is an integer`. `is`, `are`, and `be` stay the copula. `the count of`, `the length of`, `the sum of`, and `the first 3 of` stay operations.

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
| `Allow reading files, writing files, network access, changing processes, running scripts, reading secrets, asking a model` | Effect grants for commands, native tools, and bare MCP tools |
| `ensure <condition>` | Halt with `MachineHaltException` when the condition is false |
| `infer "prompt"` / `infer <expression>` | Runtime model call (`ai.infer`); needs `Allow asking a model`; result is untyped data |
| `Use payments from "payments.capability.json"` | Bind a capability manifest (mock, MCP, http, sqlite, or powershell-module) |
| `Use payments from mcp "pwsh -File server.ps1"` | Bind an MCP server directly; its tool list is the manifest |
| `Use db from sqlite "data.db"` | Bind a SQLite database; tables become typed operations |
| `Use api from http "openapi.json"` | Bind an OpenAPI document; get/post/put/delete become operations |
| `the name is <value>` | Assignment |
| `if <condition>` / `otherwise` / `otherwise if` / `otherwise when` | Branch |
| `for each name in <collection>` | Loop. The same loop can be one sentence: `for each number from 1 to 3, add it to the total`. `it` is the current item; after the sentence it is the earlier value again. `them` is the list |
| `while <condition>` / `until <condition>` | Loop. `while it is less than 3, increase it` is the same loop in one sentence. `keep increasing it until it is 3` repeats the action first |
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
| `the extension`/`file extension`/`suffix` of the path | Split-Path -Extension |
| `the name without extension`/`bare name`/`stem` of the path | Split-Path -LeafBase |
| `the full`/`absolute`/`resolved`/`complete` path of the file | Resolve-Path. Needs `Allow reading files` |
| `start`/`launch`/`open`/`run` the `program`/`app`/`application`/`executable`/`tool`/`command`/`exe`/`binary` | Start-Process. Needs `Allow changing processes` |
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
| `the environment variable HOME`, `the home folder`, `the current folder`, `the temp folder`, `the script folder` | Environment |
| `the computer name`/`machine name`/`host name`/`hostname`, `the user name`/`username`/`current user` | `[Environment]::MachineName`, `[Environment]::UserName` |
| `ask payments to list failed with the day[, retrying up to 3 times]` | Capability call; retry only for reads (`E4006` otherwise) |
| A phrase that is not one of the forms above | Open intent, matched against the command catalog or rejected |

### Types

`an integer`, `a decimal`, `a string`, `a boolean`, `a list of T`, names introduced with `An ... has`, and *untyped data* (what a command, a JSON parse, or a bare MCP tool returns). Untyped data converts to primitives at run time and must be checked with `as a <Type>` before it is used as a record (`E2014` hints at this).

## Commands (catalog-backed operations)

`tools/powershell.catalog.json` is a pinned catalog of cmdlets with English phrases, parameters, and effects: `Get-ChildItem`, `Get-Item`, `Get-Content`, `Set-Content`, `Add-Content`, `New-Item`, `New-TemporaryFile`, `Copy-Item`, `Move-Item`, `Rename-Item`, `Remove-Item`, `Test-Path`, `Join-Path`, `Split-Path`, `Resolve-Path`, `Set-Location`, `Get-FileHash`, `Select-String`, `Measure-Object`, `Compare-Object`, `Compress-Archive`, `Expand-Archive`, `Get-PSDrive`, `Get-Process`, `Stop-Process`, `Wait-Process`, `Start-Process`, `Invoke-Item`, `Get-Command`, `Get-Service`, `Start-Service`, `Stop-Service`, `Restart-Service`, `Stop-Computer`, `Restart-Computer`, `Get-Date`, `Get-TimeZone`, `Get-Uptime`, `Get-Random`, `New-Guid`, `Invoke-RestMethod`, `Invoke-WebRequest`, `Test-Connection`, `Import-Csv`, `Export-Csv`, `ConvertTo-Json`, `Get-Clipboard`, `Set-Clipboard`, `Clear-Host`, `Start-Sleep`, `Read-Host`. A sentence like `the items in the folder, files only` is matched against that catalog and becomes `Get-ChildItem -Path $folder -File`. When several phrases match, the most specific one wins. The cmdlet's effect must be granted with `Allow …` (`E4003`). Commands with write effects receive `-WhatIf` under `--dry-run`. `writ tools list <file>` counts the catalog and `writ tools inspect <file> pwsh.<Name>` prints one cmdlet's phrases, parameters, and aliases.

The sentence table's console, file, path, process, and service rows are recognized directly, including the verb and noun variants written there. A looser sentence that does not fit one of those shapes is still read from the catalog. At run time these commands are handled by the C# Machine runtime.

A catalog parameter is filled from the sentence. A name the sentence uses wins over one that is merely in scope: first a name that matches the parameter or one of its aliases (`the file` for `-Path`), then quoted text, then any other name the sentence mentions, in the order it is said (`unzip the archive into the unpacked` fills `-DestinationPath` with `unpacked`). Only when the sentence names nothing for a parameter does an in-scope name that matches an alias stand in (`list the files` with a `folder` defined). Words that belong to the phrase or to a switch (`files only`) are not names. A sentence of the shape `the X of the Y` where `Y` is text, a number, or a list, and `X` is not one of its fields, is read from the catalog too: `the hash of the file`, `the word count of the text`.

### Shell commands

The everyday shell commands, as Writ sentences. Each one is a row of the sentence table or a phrase in the catalog, and each needs the permission in its last column.

| Shell | Writ | PowerShell | Needs |
|-------|------|------------|-------|
| `ls`, `find` | `the items in the folder, files only`, `the files in the folder matching the pattern, recursively` | Get-ChildItem | reading files |
| `cat` | `the contents of the file at the path` | Get-Content -Raw | reading files |
| `head`, `tail` | `the first 3 of the lines` over a list | Select-Object | — |
| `grep` | `the lines matching the pattern in the file`, `search the file for "timeout", case sensitively` | Select-String | reading files |
| `wc` | `the word count of the text`, `count the lines in the text` | Measure-Object | — |
| `diff` | `the differences between the old and the new` | Compare-Object | — |
| `sha256sum`, `md5sum` | `the hash of the file`, `the checksum of the file` | Get-FileHash | reading files |
| `cp`, `mv`, `rm`, `mkdir`, `touch` | `copy`, `move`, `remove`, `create the folder`, `create the file` | Copy-Item, Move-Item, Remove-Item, New-Item | writing files |
| `mktemp` | `a temporary file` | New-TemporaryFile | writing files |
| `zip`, `tar -c` | `zip the folder to the archive` | Compress-Archive | writing files |
| `unzip`, `tar -x` | `unzip the archive into the unpacked` | Expand-Archive | writing files |
| `pwd`, `cd` | `the current folder`, `go to the folder` | Get-Location, Set-Location | — |
| `basename`, `dirname` | `the file name of the path`, `the extension of the path`, `the name without extension of the path`, `the parent folder of the path` | Split-Path | — |
| `realpath` | `the full path of the file` | Resolve-Path | reading files |
| `test -e` | `the file exists` | Test-Path | reading files |
| `df` | `the drives` | Get-PSDrive -PSProvider FileSystem | reading files |
| `ps`, `kill`, `wait` | `the running processes`, `stop the process`, `wait for the process to exit` | Get-Process, Stop-Process, Wait-Process | changing processes |
| `which` | `locate the command`, `the location of the command` | Get-Command | changing processes |
| `xdg-open`, `start` | `open the file` | Invoke-Item | changing processes |
| program launch | `run the command`, `start the program` | Start-Process | changing processes |
| `systemctl` | `start`/`stop`/`restart the service` | Start-Service, Stop-Service, Restart-Service | changing processes |
| `shutdown`, `reboot` | `shut down the computer`, `restart the computer` | Stop-Computer, Restart-Computer | changing processes |
| `ping` | `ping the host` | Test-Connection -Quiet | network access |
| `curl`, `wget` | `fetch json from the url`, `download the url` | Invoke-RestMethod, Invoke-WebRequest | network access |
| `date` | `the current date`, `the current date formatted as the format` | Get-Date | — |
| `uptime` | `the uptime` | Get-Uptime | — |
| `hostname`, `whoami` | `the computer name`, `the user name` | [Environment] | — |
| `env`, `echo $HOME` | `the environment variable HOME`, `the home folder` | [Environment] | — |
| `uuidgen` | `a new guid` | New-Guid | — |
| `shuf -i` | `a random number from the low to the high` (the high is not included) | Get-Random | — |
| `pbcopy`, `pbpaste` | `copy the text to the clipboard`, `the clipboard contents` | Set-Clipboard, Get-Clipboard | changing processes |
| `jq -c` | `the data converted to json` | ConvertTo-Json | — |
| `sleep`, `clear`, `read` | `wait 2 seconds`, `clear the screen`, `ask the user for "Your name"` | Start-Sleep, Clear-Host, Read-Host | — |

[`samples/constructs/shell.writ`](../samples/constructs/shell.writ) runs the file-side rows under the temp folder.

## Template readings

When a sentence leaves a type or an operation unstated, the compiler matches that text against the closed list of types and operations this language can represent. A type comes from the word list (`price` is a decimal, `quantity` is an integer) unless the sentence already states the type. An operation matches a catalog phrase exactly. When the words are present but reordered, or one word is a short inflection of the phrase (`files` for `file`), a local template index picks the nearest phrase that has at least two content words. Every content word of that phrase has to be in the sentence. The longest such phrase wins. Two phrases of that length are rejected (`E2104`). An id outside the list is rejected (`E2105`).

The index is the phrases already in the command catalog and in each capability's operation name. Adding a phrase there covers a new variant. Nothing is sent off the machine.

When the entries are operations, their order is the order they run. `but first`, `gut first`, `before`, and `after` move a step earlier, and `then` keeps spoken order.

An explicit type or definition is a specific rule and is not read again. Every accepted reading is recorded in the lockfile and shown by `writ explain`.

`--debug` on `check`, `compile`, `run`, `lock`, `explain`, `diff`, and `tools` writes each rule decision and each template reading to standard error: the sentence, the catalog that was offered, and the reply. A rule match says why the sentence was not read as a template. A failure inside the reader is `E2106`.

## Capabilities and MCP

A manifest declares types, operations, allowed operations, granted effects, and a `provider`: `mock` (results under `mock`), `mcp` with a `command` (stdio) or `baseUrl` (HTTP), `http` (OpenAPI document or base URL), `sqlite` (database path), or `powershell-module` (module name). Checking a program that needs an MCP server starts it, sends `initialize` and `tools/list`, confirms every declared operation exists, and stops. An `http` provider with a local OpenAPI file discovers get/post/put/delete operations; a `sqlite` provider reads `PRAGMA table_info` and pins a schema hash. The compile-time client has no effectful calls; invocation lives only in the Machine runtime.

Native tools live in `tools/writ.catalog.json` (merged with the PowerShell catalog). Dotted ids such as `text.trim`, `json.path`, `secret.get`, `http.post`, `store.remember`, `notify.webhook`, `compiler.check`, and `ai.infer` are ordinary catalog sentences. `writ.mocks.json` in the program folder or `~/.writ` can supply `{ "Get-Uptime": { "result": … } }` for any catalog tool.

`Use payments from mcp "…"` skips the manifest: tools become operations, input schemas become arguments, results are untyped data, read-only tools need `Allow network access`, and the rest need `Allow changing external systems`. The quoted text is a command line, or an `http://` or `https://` URL. `tools/mcp-test-server.ps1` is the in-repo server used by tests; it logs every JSON-RPC method so tests can prove `check` never calls a tool.

The same servers can be declared in `writ.mcp.json`. The compiler reads every such file from the program's folder upward, then `%USERPROFILE%\.writ\writ.mcp.json`. The closest file wins for a given alias; the user file is only the fallback. A duplicate alias in one file is an error. An explicit `Use` for the same alias wins over all of them. A server is queried only when the program asks for that alias, or when a sentence names the alias so a tool can be selected from its list. Checking still only sends `initialize` and `tools/list`.

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

Capability failures are structured: the Machine throws `WritCapabilityException` with `Alias`, `Operation`, and `Provider`; the executor reports `payments.retry (mock): …`.

## Scripts

`run "path"` calls another script. The path is relative to the file that contains the sentence. Paths are `.writ` or `.ps1`.

A Writ script is compiled while checking, which reads its `When run` parameters and result type and never runs it. The caller must grant every permission that script grants. At run time the nested program is compiled to C# and invoked in process. A script that calls itself, directly or through another script, is `E4012`. A Writ script with no `When run` block is `E4011`.

A foreign PowerShell script is not executed while checking. The compiler reads the top-level `param()` block and, when present, the `.OUTPUTS` section of the comment-based help. Without `.OUTPUTS` the result is untyped data. Because the body is invisible, running it needs `Allow running scripts` (`E4003`) and starts `pwsh` only for that file. A missing file, or a file that is neither `.writ` nor `.ps1`, is `E4010`.

`Also use` copies helpers into this program. `run` starts the other script and uses what it returns.

## Emission

Programs emit one C# library:

| Id | Language | Approach | Architecture | Offers |
|----|----------|----------|--------------|--------|
| `csharp-library` | C# | library | in-process | `dry-run`, `cmdlets`, `csharp`, `library`, `in-process` |

`--target auto` (the default) and `--target csharp` both select that library. `--target powershell` is an error. A constraint the library cannot meet, such as `forbid: ["in-process"]`, is `E5001`.

Constraints live in JSON. The project file is `writ.constraints.json`, next to the source or in a parent folder (the nearest file wins, same walk as `writ.mcp.json`). The user file is `~/.writ/constraints.json`. The command line reads both. A library call reads the project file only, unless it asks for the user file.

```json
{
  "require": ["dry-run"],
  "prefer": ["csharp", "in-process"],
  "avoid": ["cmdlets"],
  "language": "csharp",
  "approach": "library",
  "architecture": "in-process"
}
```

`require` and `forbid` from both files apply. A trait that is required and forbidden is `E5002`, as is an unknown trait or a key the file does not define. When both files set `language`, `approach`, or `architecture`, the project file wins. Preferences are scored: each project `prefer` or `avoid` counts 2, each user one counts 1.

`writ explain` prints the choice. The lockfile records the emission id. `--frozen` fails when that id changes (`E4009`). A lock written before emissions were recorded still matches.

`samples/emission` prefers the in-process C# target.

## Lockfile

`<file>.writ.lock.json` pins the source hash, compiler version, IR schema, cmdlet catalog hash, every capability's discovery hash (manifest content or MCP tool list) and operations, every template reading, and the chosen emission. `check`, `compile`, and `run` fail when a tool changed since it was locked (`E4008`); `writ lock` accepts the change. `--frozen` fails on any difference (`E4009`), including a different emission. `writ diff` says whether an edit changed meaning or only text.

## Pipeline

English source → sentences → structural AST → includes merged → typed IR (schema 0.7.0) → checks → C# library → Machine run.

Language forms Writ accepts, and the ones it leaves out, are listed in [constructs.md](constructs.md).

## Fail-closed behavior

Unknown names, unknown types, wrong arguments, non-boolean conditions, missing returns, unresolved phrases, ambiguous phrases, unknown operations, invalid manifests, missing effect grants, retried writes, missing MCP tools, drifted tools, and helper files with their own entry are errors that point at the English source.

## Native tools (writ.catalog.json)

Phrase-matched like cmdlets. Pure tools need no `Allow`. Others need the effect in the catalog row.

| Area | Examples |
|------|----------|
| Text | trim, upper/lower, slug, base64, URL encode/decode |
| Dates | add days/hours, days between, UTC now |
| Collections | distinct, reverse, flatten, skip, min/max/average |
| Data | `json.path`, `as YAML`, `as XML` (with `as JSON` / `as CSV`) |
| Secrets | `the secret NAME` (`Allow reading secrets`) |
| HTTP | `http.post` / `http.put`; also `Invoke-RestMethod` / `Invoke-WebRequest` |
| Store | `store.remember` / `store.recall` (`~/.writ/state.db`) |
| Observability | `log`, `the calls so far`, `the elapsed milliseconds` |
| Notify | webhook, email (`WRIT_SMTP_*`), Windows toast |
| Compiler | `compiler.check` / `explain` / `lock-info` |
| AI | `infer …` (`Allow asking a model`, `WRIT_AI_*`) |
| Process | `the output of running …` (stdout capture) |

Every catalog entry has an in-process Machine handler; `CatalogCoverageTests` fails if that drifts.
