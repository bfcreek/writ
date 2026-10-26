# Writ

Writ is an experimental programming language. Source text is constrained English. The compiler turns those sentences into typed operations and then into a PowerShell 7 script. A sentence that leaves a type or an operation unstated is matched against a closed catalog, or rejected. PowerShell cmdlets and MCP tools are ordinary sentences.

This is a language, not a free-form prompt. The supported forms are listed in [docs/SUPPORTED.md](docs/SUPPORTED.md).

## A record and a function

[`samples/basics/calculate-total.writ`](samples/basics/calculate-total.writ) declares a record and a function. `a tax rate` becomes `taxRate` in the typed program and in JSON input.

```text
An InvoiceItem has
    a price which is a decimal
    and a quantity which is an integer

To CalculateTotal, given items which are a list of InvoiceItem and a tax rate which is a decimal, returning a decimal
    the subtotal is the sum of price times quantity for each item in items
    the tax is the subtotal times the tax rate
    return the subtotal plus the tax rounded to 2 places
```

```json
{
  "items": [
    { "price": 10.00, "quantity": 2 },
    { "price": 5.50, "quantity": 1 }
  ],
  "taxRate": 0.08
}
```

```text
27.54
```

## Branches and a loop

[`samples/basics/fizzbuzz.writ`](samples/basics/fizzbuzz.writ) is one integer. [`samples/basics/fizzbuzz-loop.writ`](samples/basics/fizzbuzz-loop.writ) repeats the same test for each integer in a list.

```text
To Classify, given n which is an integer, returning a string
    if n modulo 15 is equal to 0
        return "FizzBuzz"
    otherwise if n modulo 3 is equal to 0
        return "Fizz"
    otherwise if n modulo 5 is equal to 0
        return "Buzz"
    otherwise
        return n as a string
```

`ClassifyAll` on `[1, 3, 5, 15, 7]` returns:

```text
[
  "1",
  "Fizz",
  "Buzz",
  "FizzBuzz",
  "7"
]
```

## A script

[`samples/files/largest-files.writ`](samples/files/largest-files.writ) is what runs when you start the script. `Allow` grants an effect. `Also use` brings in another file. `the items in the folder, files only` is a catalog sentence for `Get-ChildItem`.

```text
# Show the largest files in a folder.
# Defaults to the current folder when no folder is given.
Allow reading files
Also use "folder-helpers.writ"

When run, given a folder which is a string, returning a list of string
    if the folder is equal to ""
        the folder is the current folder
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

`Describe` lives in [`samples/files/folder-helpers.writ`](samples/files/folder-helpers.writ) and returns `"{the name} ({the size} bytes)"`.

## A tool

[`samples/payments/recover-failures.writ`](samples/payments/recover-failures.writ) binds a capability manifest and calls it with `ask`.

```text
Use payments from "payments.capability.json"

To RecoverFailures, given a day which is a string, returning an integer
    the failures are ask payments to list failed with the day
    the recovered is 0
    for each payment in the failures
        if the retryCount of the payment is less than 3
            the result is ask payments to retry with the id of the payment
            if the succeeded of the result is true
                the recovered is the recovered plus 1
    return the recovered
```

The same shape works against an MCP server. See [`samples/payments/mcp-payments.writ`](samples/payments/mcp-payments.writ).

## Run a program

Building from source needs the .NET 8 SDK. Running a generated script needs PowerShell 7.

`writ` below means `dotnet run --project src/Writ.Cli --` until the tool is on your `PATH`.

```powershell
dotnet test Writ.sln
dotnet run --project src/Writ.Cli -- run samples/basics/calculate-total.writ --entry CalculateTotal --input samples/basics/input-calculate-total.json
dotnet run --project src/Writ.Cli -- run samples/basics/fizzbuzz-loop.writ --entry ClassifyAll --input samples/basics/input-fizzbuzz.json
dotnet run --project src/Writ.Cli -- run samples/files/largest-files.writ --entry Main --input samples/files/input-folder.json
dotnet run --project src/Writ.Cli -- run samples/payments/recover-failures.writ --entry RecoverFailures --input samples/payments/input-recover.json
dotnet run --project src/Writ.Cli -- explain samples/files/largest-files.writ
dotnet run --project src/Writ.Cli -- check samples/errors/retry-a-write.writ
```

| Command | What it does |
|---------|----------------|
| `writ check <file.writ>` | Accepts the program or prints the diagnostics |
| `writ compile <file.writ>` | Writes `<name>.ps1`, `<name>.ir.json`, and `<name>.writ.lock.json` |
| `writ run <file.writ> --entry Name` | Compiles and runs that function. `--input` is a JSON object of arguments |
| `writ explain <file.writ>` | Shows how each sentence was read |
| `writ diff <file.writ>` | Reviews semantic changes against the lock file |

`writ compile` installs the shared runtime module at `~/.writ/modules/Writ/Writ.psm1`. The script imports that module and runs as `pwsh ./<name>.ps1 -param value`.

With `--target auto` (the default), constraints in `writ.constraints.json` or `~/.writ/constraints.json` can require or forbid traits of the script. `--target powershell` skips that choice. `--dry-run` passes `-WhatIf` to commands that write.

More programs are grouped under [`samples/`](samples/README.md).

## Install

The Windows installer is per-user and does not ask for administrator rights. It copies `writ.exe` to `%LOCALAPPDATA%\Writ`, adds that folder to the user `PATH`, and installs the runtime module at `~/.writ/modules/Writ/Writ.psm1`. Open a new terminal after install. Generated scripts still need PowerShell 7. Build one locally with `installer/build.ps1 -Version 1.2.3 -Runtime win-x64` (or `win-arm64`).

`writ version` prints the version stamped into that build.

## Further reading

- [docs/SUPPORTED.md](docs/SUPPORTED.md) — commands, types, and the English forms the compiler accepts
- [docs/constructs.md](docs/constructs.md) — control flow, pronouns, and PowerShell constructs
- [samples/README.md](samples/README.md) — every sample, grouped by what it shows
- [scratch/intent-language-proposal.md](scratch/intent-language-proposal.md) — the design brief
- [scratch/powershell-plan.md](scratch/powershell-plan.md) — the PowerShell target plan

## Releases

Push a tag `v1.2.3` or a prerelease tag `v1.2.3-preview.1`. `tools/release.ps1` runs the tests, writes release notes from commits since the latest `vX.Y.Z` tag, commits local changes, bumps the patch version, and pushes the branch and that tag. The release workflow tests that commit, then attaches self-contained binaries, Windows installers, and a .NET tool package to the GitHub release.

This repository is public and read-only.
