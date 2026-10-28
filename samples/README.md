# Samples

Programs are grouped by what they show. `writ` below means `dotnet run --project src/Writ.Cli --`.

## basics

Records, functions, branches, and loops.

| File | What it shows |
|------|----------------|
| `calculate-total.writ` | A record, a function, a sum over a list, and rounding |
| `innate-total.writ` | The same total when field types are left unstated |
| `intent-sum.writ` | A sum the compiler fills in from the catalog |
| `intent-filter.writ` | `where` over a list |
| `fizzbuzz.writ` | `if`, `otherwise if`, and `otherwise` |
| `fizzbuzz-loop.writ` | A loop that calls another function |
| `countdown.writ` | `while` and `say` |

## files

| File | What it shows |
|------|----------------|
| `largest-files.writ` | `When run`, `Also use`, sort, take, and a helper |
| `folder-helpers.writ` | A function another file includes |
| `folder-report.writ` | `Get-ChildItem`, filter, sort, and take |

## data

| File | What it shows |
|------|----------------|
| `json-total.writ` | Read a file, parse JSON, and check it against a record |
| `rest-total.writ` | `fetch JSON` and filter the result |

## payments

Mock capabilities, retry, and MCP. Manifests live in this folder.

| File | What it shows |
|------|----------------|
| `recover-failures.writ` | `ask`, a loop, and a field test |
| `careful-retry.writ` | Retry, `try`, `if that fails`, and `finally` |
| `but-first.writ` | Steps reordered with `but first` |
| `mcp-payments.writ` | An MCP server named in the program |
| `recover-failures-mcp.writ` | The same recovery through a capability manifest |

## scripts

`call-both.writ` runs `greet.ps1` and `double.writ` and joins what they return.

## constructs

One area of the language in each file. `When run` is the entry, named `Main`.

| File | What it shows |
|------|----------------|
| `control.writ` | `if`, `otherwise if`, `for`, `while`, `repeat`/`until`, `switch`, `continue`, `break to` |
| `paragraphs.writ` | A loop as one sentence, `it`, `them`, `skip`, and `keep`/`until` |
| `values.writ` | Arithmetic, comparisons, text, lists, tables, take, sum, and a splatted call |
| `declarations.writ` | Records with methods, an enumeration, parameter checks, `filter`, `command`, modules, `trap`, and redirection |
| `host.writ` | Clear, warn, note, pause, `say`, locations, and environment |
| `failures.writ` | `try`, `if that fails`, `finally`, and `stop with` |
| `processes.writ` | Start a program, and start, stop, or restart a service. Running the script does not do those things |
| `shell.writ` | The shell commands: hash, grep, zip and unzip, word count, path parts, a guid, a random number, and the machine and user names, all under the temp folder |

```powershell
dotnet run --project src/Writ.Cli -- run samples/constructs/control.writ --entry Main
dotnet run --project src/Writ.Cli -- run samples/constructs/values.writ --entry Main
```

## complex

Larger programs that combine the forms above.

| File | What it shows |
|------|----------------|
| `billing/statement.writ` | Included pricing rules, JSON, a tax `switch`, filter, sum, and a printed statement. Input is `billing/input.json`. `TotalFile` reads `billing/invoice.json` |
| `folder-brief.writ` | A folder report with size labels, a helper from `files/`, and `try`. An empty folder means the script folder |
| `workshop.writ` | Create, write, append, copy, move, rename, read, parse CSV, and remove a folder under the temp folder |

```powershell
dotnet run --project src/Writ.Cli -- run samples/complex/billing/statement.writ --entry Main --input samples/complex/billing/input.json
dotnet run --project src/Writ.Cli -- run samples/complex/folder-brief.writ --entry Main --input samples/complex/input-folder.json
dotnet run --project src/Writ.Cli -- run samples/complex/workshop.writ --entry Main
```

## errors

Programs that `writ check` rejects. Each file is one mistake.

## emission, model, shared

`emission/` prefers a script that can dry-run. `model/` is a model profile next to a small program. `shared/` finds its payments server in `writ.mcp.json`.
