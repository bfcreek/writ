# Writ

Experimental natural-language programming language. English sentences compile to typed operations and then to a PowerShell 7 script. Constraints in `writ.constraints.json` or `~/.writ/constraints.json` can require or forbid traits of that script; `--target powershell` skips the choice. A sentence that leaves a type or an operation unstated is read by the model in `writ.model.json` or `~/.writ/model.json` (Ollama, OpenAI, Copilot, or another OpenAI-compatible server); with no file, the built-in stand-in reads it. PowerShell cmdlets and MCP tools are ordinary sentences. See [docs/SUPPORTED.md](docs/SUPPORTED.md), [scratch/powershell-plan.md](scratch/powershell-plan.md), and [scratch/intent-language-proposal.md](scratch/intent-language-proposal.md).

```powershell
dotnet test Writ.sln
dotnet run --project src/Writ.Cli -- run samples/largest-files.writ --entry Main --input samples/input-folder.json
dotnet run --project src/Writ.Cli -- run samples/mcp-payments.writ --entry RecoverFailures --input samples/input-recover.json
dotnet run --project src/Writ.Cli -- run samples/scripts/call-both.writ --entry Main --input samples/scripts/input.json
dotnet run --project src/Writ.Cli -- run samples/shared/recover.writ --entry RecoverFailures --input samples/input-recover.json
dotnet run --project src/Writ.Cli -- run samples/careful-retry.writ --entry RecoverCarefully --input samples/input-recover.json --dry-run
dotnet run --project src/Writ.Cli -- explain samples/largest-files.writ
dotnet run --project src/Writ.Cli -- check samples/errors/retry-a-write.writ
```

Compiling a program (`writ compile`) writes `<name>.ps1` beside `Writ.psm1`. The script runs with `pwsh ./<name>.ps1 -param value`. `--target powershell` skips constraint scoring.
