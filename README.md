# Writ

Experimental natural-language programming language. English sentences compile to typed operations and then to an emission: a PowerShell 7 script or an in-process C# library. Compilation picks that emission from `writ.constraints.json` or `~/.writ/constraints.json` unless `--target` forces one. A sentence that leaves a type or an operation unstated is read by the model in `writ.model.json` or `~/.writ/model.json` (Ollama, OpenAI, Copilot, or another OpenAI-compatible server); with no file, the built-in stand-in reads it. PowerShell cmdlets and MCP tools are ordinary sentences. See [docs/SUPPORTED.md](docs/SUPPORTED.md), [scratch/powershell-plan.md](scratch/powershell-plan.md), and [scratch/intent-language-proposal.md](scratch/intent-language-proposal.md).

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

Compiling a program (`writ compile`) writes the chosen emission. The default, a PowerShell script, is `<name>.ps1` beside `Writ.psm1` and runs with `pwsh ./<name>.ps1 -param value`. A project that prefers an in-process library, such as `samples/emission`, writes `<name>.generated.cs` instead. `--target powershell` or `--target dotnet` skips the choice.
