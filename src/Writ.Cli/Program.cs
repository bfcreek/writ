using System.CommandLine;
using Writ.Compiler.Codegen;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Semantics;

var fileArgument = new Argument<FileInfo>("file")
{
    Description = "Path to a .writ source file"
};

var outOption = new Option<DirectoryInfo?>("--out")
{
    Description = "Output directory for generated artifacts"
};

var entryOption = new Option<string>("--entry")
{
    Description = "Entry function name to invoke",
    Required = true
};

var inputOption = new Option<FileInfo?>("--input")
{
    Description = "JSON object of argument values for the entry function"
};

var targetOption = new Option<string?>("--target")
{
    Description = "Emission to write: auto (default), powershell, or dotnet. auto reads writ.constraints.json and ~/.writ/constraints.json"
};

var frozenOption = new Option<bool>("--frozen")
{
    Description = "Fail unless the lockfile matches exactly (source, compiler, tools, model readings)"
};

var modelOption = new Option<string?>("--model")
{
    Description = "Model profile in writ.model.json or ~/.writ/model.json. The file's \"use\" applies when this is omitted"
};

var checkCommand = new Command("check", "Parse, bind, and validate an Writ program")
{
    fileArgument,
    frozenOption,
    modelOption
};
checkCommand.SetAction(parseResult => RunCheck(
    parseResult.GetValue(fileArgument)!,
    parseResult.GetValue(frozenOption),
    parseResult.GetValue(modelOption)));

var compileCommand = new Command("compile", "Compile an Writ program. Constraints choose the emission, unless --target forces one")
{
    fileArgument,
    outOption,
    targetOption,
    frozenOption,
    modelOption
};
compileCommand.SetAction(parseResult => RunCompile(
    parseResult.GetValue(fileArgument)!,
    parseResult.GetValue(outOption),
    parseResult.GetValue(targetOption)!,
    parseResult.GetValue(frozenOption),
    parseResult.GetValue(modelOption)));

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Run with -WhatIf: commands and capability calls that change something are described, not performed"
};

var runCommand = new Command("run", "Compile and execute an Writ program")
{
    fileArgument,
    entryOption,
    inputOption,
    targetOption,
    dryRunOption,
    modelOption
};
runCommand.SetAction(parseResult => RunExecute(
    parseResult.GetValue(fileArgument)!,
    parseResult.GetValue(entryOption)!,
    parseResult.GetValue(inputOption),
    parseResult.GetValue(targetOption)!,
    parseResult.GetValue(dryRunOption),
    parseResult.GetValue(modelOption)));

var lockCommand = new Command("lock", "Write or refresh the lockfile for a program (accepts changed tools)")
{
    fileArgument,
    modelOption
};
lockCommand.SetAction(parseResult => RunLock(parseResult.GetValue(fileArgument)!, parseResult.GetValue(modelOption)));

var toolsListCommand = new Command("list", "List the capabilities, operations, and granted permissions a program can use")
{
    fileArgument,
    modelOption
};
toolsListCommand.SetAction(parseResult => RunToolsList(parseResult.GetValue(fileArgument)!, parseResult.GetValue(modelOption)));

var operationArgument = new Argument<string>("operation") { Description = "alias.operation or pwsh.Cmdlet-Name" };
var toolsInspectCommand = new Command("inspect", "Show one operation: arguments, result, effects, and provider")
{
    fileArgument,
    operationArgument,
    modelOption
};
toolsInspectCommand.SetAction(parseResult => RunToolsInspect(
    parseResult.GetValue(fileArgument)!,
    parseResult.GetValue(operationArgument)!,
    parseResult.GetValue(modelOption)));

var toolsCommand = new Command("tools", "Discover the tools a program binds to (never invokes them)")
{
    toolsListCommand,
    toolsInspectCommand
};

var explainCommand = new Command("explain", "Show what each sentence bound to: tools, effects, model readings")
{
    fileArgument,
    modelOption
};
explainCommand.SetAction(parseResult => RunExplain(parseResult.GetValue(fileArgument)!, parseResult.GetValue(modelOption)));

var diffCommand = new Command("diff", "Show semantic changes between the lockfile and the program as it binds now")
{
    fileArgument,
    modelOption
};
diffCommand.SetAction(parseResult => RunDiff(parseResult.GetValue(fileArgument)!, parseResult.GetValue(modelOption)));

var root = new RootCommand("Writ compiler - English source to typed operations")
{
    checkCommand,
    compileCommand,
    runCommand,
    lockCommand,
    toolsCommand,
    explainCommand,
    diffCommand
};

return root.Parse(args).Invoke();

static CompilationResult? CompileFile(FileInfo file, CompilationPipeline pipeline, bool frozen, bool writeLock, EmissionOptions emission, ModelOptions model)
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return null;
    }

    var source = File.ReadAllText(file.FullName);
    var result = pipeline.Compile(source, file.FullName, emission: emission, model: model);
    PrintDiagnostics(result.Diagnostics);
    if (!result.Success)
    {
        return null;
    }

    var lockPath = LockFile.PathFor(file.FullName);
    var current = LockFile.Build(source, result, pipeline.Cmdlets);
    var existing = LockFile.Read(lockPath);
    var lockDiagnostics = LockFile.Verify(existing, current, file.FullName, frozen);
    PrintDiagnostics(lockDiagnostics);
    if (lockDiagnostics.Count > 0)
    {
        return null;
    }

    if (writeLock)
    {
        LockFile.Write(lockPath, current);
        Console.WriteLine($"Wrote {lockPath}");
    }

    return result;
}

static int RunCheck(FileInfo file, bool frozen, string? model)
{
    var result = CompileFile(file, new CompilationPipeline(), frozen, writeLock: false, AutoEmission(), AutoModel(model));
    if (result is null)
    {
        return 1;
    }

    Console.WriteLine($"OK {file.Name}");
    PrintModel(result.Model);
    PrintEmission(result.Emission);
    return 0;
}

static int RunLock(FileInfo file, string? model)
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    var pipeline = new CompilationPipeline();
    var source = File.ReadAllText(file.FullName);
    var result = pipeline.Compile(source, file.FullName, emission: AutoEmission(), model: AutoModel(model));
    PrintDiagnostics(result.Diagnostics);
    if (!result.Success)
    {
        return 1;
    }

    var lockPath = LockFile.PathFor(file.FullName);
    LockFile.Write(lockPath, LockFile.Build(source, result, pipeline.Cmdlets));
    Console.WriteLine($"Wrote {lockPath}");
    return 0;
}

static int RunExplain(FileInfo file, string? model)
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    var result = new CompilationPipeline().Compile(File.ReadAllText(file.FullName), file.FullName, emission: AutoEmission(), model: AutoModel(model));
    PrintDiagnostics(result.Diagnostics);
    Console.Write(Explainer.Explain(result, file.FullName));
    return result.Success ? 0 : 1;
}

static int RunDiff(FileInfo file, string? model)
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    var pipeline = new CompilationPipeline();
    var source = File.ReadAllText(file.FullName);
    var result = pipeline.Compile(source, file.FullName, emission: AutoEmission(), model: AutoModel(model));
    PrintDiagnostics(result.Diagnostics);
    if (!result.Success)
    {
        return 1;
    }

    var current = LockFile.Build(source, result, pipeline.Cmdlets);
    var existing = LockFile.Read(LockFile.PathFor(file.FullName));
    Console.Write(Explainer.Diff(existing, current));
    return 0;
}

static int RunToolsList(FileInfo file, string? model)
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    var pipeline = new CompilationPipeline();
    var result = pipeline.Compile(File.ReadAllText(file.FullName), file.FullName, emission: AutoEmission(), model: AutoModel(model));
    PrintDiagnostics(result.Diagnostics.Where(d => d.Code.StartsWith("E4", StringComparison.Ordinal) || d.Code.StartsWith("E5", StringComparison.Ordinal) || d.Code == "E2107").ToList());
    if (result.Diagnostics.Any(d => d.Code == "E2107"))
    {
        return 1;
    }

    Console.WriteLine($"Permissions: {(result.GrantedEffects.Count == 0 ? "(none)" : string.Join(", ", result.GrantedEffects))}");
    foreach (var capability in result.Capabilities)
    {
        Console.WriteLine($"{capability.Alias}  provider={capability.Provider}  source={capability.Source}  hash={capability.DiscoveryHash}");
        foreach (var operation in capability.Operations.Values)
        {
            var allowed = capability.AllowedOperations.Count == 0 || capability.AllowedOperations.Contains(operation.Name);
            var args = string.Join(", ", operation.Arguments.Select(a => $"{a.Name}: {DescribeType(a.Type)}{(a.Required ? "" : "?")}"));
            Console.WriteLine($"  {(allowed ? " " : "x")} {capability.Alias}.{operation.Name}({args}) -> {DescribeType(operation.ResultType)}  [{string.Join(", ", operation.Effects)}]");
        }
    }

    var cmdlets = pipeline.Cmdlets.AsCatalogEntries().Count();
    Console.WriteLine($"PowerShell cmdlet catalog: {cmdlets} cmdlets ({pipeline.Cmdlets.Hash[..19]}...); use 'writ tools inspect <file> pwsh.<Name>' for one.");
    return 0;
}

static int RunToolsInspect(FileInfo file, string operation, string? model)
{
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    var pipeline = new CompilationPipeline();
    var result = pipeline.Compile(File.ReadAllText(file.FullName), file.FullName, emission: AutoEmission(), model: AutoModel(model));
    var modelErrors = result.Diagnostics.Where(d => d.Code == "E2107").ToList();
    if (modelErrors.Count > 0)
    {
        PrintDiagnostics(modelErrors);
        return 1;
    }
    if (operation.StartsWith("pwsh.", StringComparison.OrdinalIgnoreCase))
    {
        var entry = pipeline.Cmdlets.Find(operation[5..]);
        if (entry is null)
        {
            Console.Error.WriteLine($"No cmdlet named '{operation[5..]}' in the catalog.");
            return 1;
        }

        Console.WriteLine($"{entry.Name}  effect={entry.Effect}  output={DescribeType(entry.Output)}");
        Console.WriteLine($"  phrases: {string.Join(" | ", entry.Phrases)}");
        foreach (var parameter in entry.Parameters)
        {
            Console.WriteLine($"  -{parameter.Name}: {DescribeType(parameter.Type)}{(parameter.Required ? " (required)" : "")}  aliases: {string.Join(", ", parameter.Aliases)}");
        }

        Console.WriteLine($"  granted here: {(result.GrantedEffects.Contains(entry.Effect) || entry.Effect == Writ.Compiler.Ir.EffectKind.Pure ? "yes" : "no")}");
        return 0;
    }

    var dot = operation.IndexOf('.');
    var alias = dot < 0 ? operation : operation[..dot];
    var name = dot < 0 ? null : operation[(dot + 1)..];
    var capability = result.Capabilities.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
    if (capability is null)
    {
        Console.Error.WriteLine($"No capability '{alias}' in {file.Name}.");
        return 1;
    }

    Console.WriteLine($"{capability.Alias}  provider={capability.Provider}  source={capability.Source}");
    Console.WriteLine($"  hash={capability.DiscoveryHash}");
    Console.WriteLine($"  grants: {(capability.GrantedEffects.Count == 0 ? "(none; program Allow applies)" : string.Join(", ", capability.GrantedEffects))}");
    foreach (var op in capability.Operations.Values.Where(o => name is null || o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"  {op.Name} -> {DescribeType(op.ResultType)}  effects: {string.Join(", ", op.Effects)}");
        foreach (var argument in op.Arguments)
        {
            Console.WriteLine($"    {argument.Name}: {DescribeType(argument.Type)}{(argument.Required ? " (required)" : "")}");
        }
    }

    return 0;
}

static string DescribeType(Writ.Compiler.Ir.IrType type) => type switch
{
    Writ.Compiler.Ir.IrPrimitiveType p => p.Name,
    Writ.Compiler.Ir.IrListType l => $"list of {DescribeType(l.ElementType)}",
    Writ.Compiler.Ir.IrRecordType r => r.Name,
    Writ.Compiler.Ir.IrDynamicType => "dynamic",
    Writ.Compiler.Ir.IrVoidType => "nothing",
    _ => type.GetType().Name
};

static int RunCompile(FileInfo file, DirectoryInfo? outDir, string? target, bool frozen, string? model)
{
    if (!TryEmission(target, out var emission))
    {
        return 1;
    }

    var pipeline = new CompilationPipeline();
    var result = CompileFile(file, pipeline, frozen, writeLock: !frozen, emission, AutoModel(model));
    if (result is null || result.Ir is null)
    {
        return 1;
    }

    var directory = outDir?.FullName ?? Path.Combine(file.DirectoryName ?? ".", "out");
    Directory.CreateDirectory(directory);
    var baseName = Path.GetFileNameWithoutExtension(file.Name);
    var written = new List<string>();
    PrintModel(result.Model);
    PrintEmission(result.Emission);
    if (IsCSharp(result.Emission))
    {
        var csPath = Path.Combine(directory, $"{baseName}.generated.cs");
        File.WriteAllText(csPath, result.GeneratedCSharp);
        written.Add(csPath);
    }
    else
    {
        var scriptPath = Path.Combine(directory, $"{baseName}.ps1");
        File.WriteAllText(scriptPath, result.GeneratedPowerShell);
        var modulePath = Path.Combine(directory, "Writ.psm1");
        File.WriteAllText(modulePath, PowerShellGenerator.RuntimeModuleSource);
        written.Add(scriptPath);
        written.Add(modulePath);
    }

    var irPath = Path.Combine(directory, $"{baseName}.ir.json");
    File.WriteAllText(irPath, pipeline.SerializeIr(result.Ir));
    written.Add(irPath);
    foreach (var path in written)
    {
        Console.WriteLine($"Wrote {path}");
    }

    return 0;
}

static int RunExecute(FileInfo file, string entry, FileInfo? inputFile, string? target, bool dryRun, string? model)
{
    if (!TryEmission(target, out var emission))
    {
        return 1;
    }

    var result = CompileFile(file, new CompilationPipeline(), frozen: false, writeLock: false, emission, AutoModel(model));
    if (result is null)
    {
        return 1;
    }

    PrintModel(result.Model);
    PrintEmission(result.Emission);
    if (dryRun && IsCSharp(result.Emission))
    {
        Console.Error.WriteLine("--dry-run applies to the PowerShell script emission. This program chose csharp-library.");
        return 1;
    }

    string? inputJson = null;
    if (inputFile is not null)
    {
        if (!inputFile.Exists)
        {
            Console.Error.WriteLine($"Input file not found: {inputFile.FullName}");
            return 1;
        }

        inputJson = File.ReadAllText(inputFile.FullName);
    }

    var moduleName = Path.GetFileNameWithoutExtension(file.Name);
    var exec = IsCSharp(result.Emission)
        ? new RoslynExecutor().Execute(result.GeneratedCSharp!, moduleName, entry, inputJson)
        : new PowerShellExecutor { DryRun = dryRun }.Execute(result.GeneratedPowerShell!, moduleName, entry, inputJson);
    if (!exec.Success)
    {
        foreach (var error in exec.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return 1;
    }

    if (!string.IsNullOrWhiteSpace(exec.Output))
    {
        Console.Write(exec.Output);
    }

    Console.WriteLine(exec.FormatValue());
    return 0;
}

static EmissionOptions AutoEmission() => new() { ReadUserFile = true };

static ModelOptions AutoModel(string? profile) => new()
{
    ReadUserFile = true,
    Profile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim()
};

static void PrintModel(ModelChoice? model)
{
    if (model is null)
    {
        return;
    }

    var named = string.IsNullOrEmpty(model.Model) ? model.Provider : $"{model.Provider} {model.Model}";
    var profile = string.IsNullOrEmpty(model.Profile) ? "" : $" ({model.Profile})";
    var source = model.Source == "built-in" ? "built-in" : Path.GetFileName(model.Source);
    Console.WriteLine($"Model: {named}{profile} from {source}");
}

static bool TryEmission(string? target, out EmissionOptions emission)
{
    if (!EmissionCatalog.TryParseTarget(target, out var forced, out var error))
    {
        Console.Error.WriteLine(error);
        emission = EmissionOptions.ProjectOnly;
        return false;
    }

    emission = new EmissionOptions { ForcedId = forced, ReadUserFile = true };
    return true;
}

static bool IsCSharp(EmissionDecision? decision) =>
    decision?.Id == EmissionCatalog.CSharpLibrary.Id;

static void PrintEmission(EmissionDecision? decision)
{
    if (decision is null)
    {
        return;
    }

    Console.WriteLine($"Emission: {decision.Id} — {decision.Summary}");
    Console.WriteLine($"  {decision.Reason}");
    foreach (var other in decision.Considered)
    {
        Console.WriteLine($"  {other.Id}: {other.Reason}");
    }
}

static void PrintDiagnostics(IReadOnlyList<Writ.Compiler.Diagnostics.Diagnostic> diagnostics)
{
    foreach (var diagnostic in diagnostics)
    {
        var writer = diagnostic.Severity == Writ.Compiler.Diagnostics.DiagnosticSeverity.Error
            ? Console.Error
            : Console.Out;
        writer.WriteLine(diagnostic.ToString());
    }
}
