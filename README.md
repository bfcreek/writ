# Writ

This repository is public and read-only.

Experimental natural-language programming language. English sentences compile to typed operations and then to a PowerShell 7 script. Constraints in `writ.constraints.json` or `~/.writ/constraints.json` can require or forbid traits of that script; `--target powershell` skips the choice. A sentence that leaves a type or an operation unstated is matched against the closed catalog: an exact phrase wins, and a reordered or lightly inflected phrase falls back to a local template index. PowerShell cmdlets and MCP tools are ordinary sentences. See [docs/SUPPORTED.md](docs/SUPPORTED.md), [scratch/powershell-plan.md](scratch/powershell-plan.md), and [scratch/intent-language-proposal.md](scratch/intent-language-proposal.md).

Samples are grouped under `samples/`. See [samples/README.md](samples/README.md).

```powershell
dotnet test Writ.sln
dotnet run --project src/Writ.Cli -- run samples/files/largest-files.writ --entry Main --input samples/files/input-folder.json
dotnet run --project src/Writ.Cli -- run samples/payments/mcp-payments.writ --entry RecoverFailures --input samples/payments/input-recover.json
dotnet run --project src/Writ.Cli -- run samples/scripts/call-both.writ --entry Main --input samples/scripts/input.json
dotnet run --project src/Writ.Cli -- run samples/shared/recover.writ --entry RecoverFailures --input samples/payments/input-recover.json
dotnet run --project src/Writ.Cli -- run samples/payments/careful-retry.writ --entry RecoverCarefully --input samples/payments/input-recover.json --dry-run
dotnet run --project src/Writ.Cli -- run samples/constructs/control.writ --entry Main
dotnet run --project src/Writ.Cli -- run samples/complex/billing/statement.writ --entry Main --input samples/complex/billing/input.json
dotnet run --project src/Writ.Cli -- explain samples/files/largest-files.writ
dotnet run --project src/Writ.Cli -- check samples/errors/retry-a-write.writ
```

Compiling a program (`writ compile`) writes `<name>.ps1` beside `Writ.psm1`. The script runs with `pwsh ./<name>.ps1 -param value`. `--target powershell` skips constraint scoring.

## Releases

Push a tag `v1.2.3` or a prerelease tag `v1.2.3-preview.1`. The release workflow tests that commit, then attaches self-contained binaries and a .NET tool package to the GitHub release. `writ version` prints the version stamped into that build.
