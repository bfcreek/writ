using System.Text.Json;
using System.Text.Json.Serialization;
using Writ.Compiler.Capabilities;
using Writ.Compiler.Binding;
using Writ.Compiler.Codegen;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;
using Writ.Compiler.Syntax;
using Writ.Compiler.Validation;

namespace Writ.Compiler.Compilation;

public sealed class CompilationPipeline
{
    private static readonly JsonSerializerOptions IrJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new IrLiteralValueConverter() }
    };

    private readonly ISemanticResolver _semanticResolver;
    private readonly ICapabilityLoader _capabilities;
    private readonly ILanguageModel _model;
    private readonly CmdletCatalog _cmdlets;

    public CompilationPipeline(
        ISemanticResolver? semanticResolver = null,
        ICapabilityLoader? capabilities = null,
        ILanguageModel? model = null,
        CmdletCatalog? cmdlets = null)
    {
        _semanticResolver = semanticResolver ?? new RuleBasedSemanticResolver();
        _capabilities = capabilities ?? new FileCapabilityLoader();
        _model = model ?? new InnateLanguageModel();
        _cmdlets = cmdlets ?? CmdletCatalog.Default;
    }

    public CompilationResult Compile(
        string source,
        string filePath,
        string? moduleName = null,
        EmissionOptions? emission = null,
        ModelOptions? model = null)
    {
        var diagnostics = new DiagnosticBag();
        var name = moduleName ?? Path.GetFileNameWithoutExtension(filePath);
        var languageModel = _model;

        CompilationResult Finish(CompilationResult result, Binder? binder = null) =>
            binder is null ? result : WithBinding(result, binder);

        var lexer = new Lexer(source, filePath, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
        {
            return Finish(CompilationResult.Failed(diagnostics.Items));
        }

        var parser = new Parser(tokens, filePath, diagnostics);
        var ast = parser.ParseProgram();
        if (diagnostics.HasErrors)
        {
            return Finish(new CompilationResult(false, diagnostics.Items, ast, null, null));
        }

        ast = ResolveIncludes(ast, filePath, diagnostics, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (diagnostics.HasErrors)
        {
            return Finish(new CompilationResult(false, diagnostics.Items, ast, null, null));
        }

        var binder = new Binder(filePath, diagnostics, _semanticResolver, _capabilities, languageModel, _cmdlets, model?.Debug);
        var ir = binder.Bind(ast, name);
        if (diagnostics.HasErrors)
        {
            return Finish(new CompilationResult(false, diagnostics.Items, ast, ir, null), binder);
        }

        var validator = new Validator(filePath, diagnostics);
        validator.Validate(ir);
        if (diagnostics.HasErrors)
        {
            return Finish(new CompilationResult(false, diagnostics.Items, ast, ir, null), binder);
        }

        var decision = EmissionSelector.Select(ir, filePath, emission ?? EmissionOptions.ProjectOnly, diagnostics);
        if (decision is null)
        {
            return Finish(new CompilationResult(false, diagnostics.Items, ast, ir, null), binder);
        }

        string? powershell = null;
        string? csharp = null;
        if (decision.Id.Equals(EmissionCatalog.CSharpLibrary.Id, StringComparison.OrdinalIgnoreCase))
        {
            csharp = new CSharpGenerator().Generate(ir, filePath);
        }
        else
        {
            powershell = new PowerShellGenerator().Generate(ir, filePath);
        }

        return Finish(new CompilationResult(true, diagnostics.Items, ast, ir, powershell), binder) with
        {
            Emission = decision,
            GeneratedCSharp = csharp
        };
    }

    public CmdletCatalog Cmdlets => _cmdlets;

    /// <summary>
    /// "Also use "helpers.writ"": the other file's types, functions, capabilities, and grants join this
    /// program. Helper files may not declare their own "When run" block.
    /// </summary>
    private static ProgramAst ResolveIncludes(ProgramAst program, string filePath, DiagnosticBag diagnostics, HashSet<string> seen)
    {
        var fullPath = Path.GetFullPath(filePath);
        seen.Add(fullPath);
        var includes = program.Declarations.OfType<IncludeDeclAst>().ToList();
        if (includes.Count == 0)
        {
            return program;
        }

        var merged = program.Declarations.Where(d => d is not IncludeDeclAst).ToList();
        foreach (var include in includes)
        {
            var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var includePath = Path.GetFullPath(Path.Combine(directory, include.Path));
            if (seen.Contains(includePath))
            {
                continue;
            }

            if (!File.Exists(includePath))
            {
                diagnostics.Error("E1107", $"Could not find \"{include.Path}\" next to {Path.GetFileName(filePath)}.", filePath, include.Span);
                continue;
            }

            var includedDiagnostics = new DiagnosticBag();
            var tokens = new Lexer(File.ReadAllText(includePath), includePath, includedDiagnostics).Tokenize();
            var included = new Parser(tokens, includePath, includedDiagnostics).ParseProgram();
            diagnostics.AddRange(includedDiagnostics.Items);
            if (includedDiagnostics.HasErrors)
            {
                continue;
            }

            included = ResolveIncludes(included, includePath, diagnostics, seen);
            foreach (var declaration in included.Declarations)
            {
                if (declaration is FunctionAst { IsEntry: true })
                {
                    diagnostics.Error("E1108", $"\"{include.Path}\" says what to do when it runs; only the main file may do that.", filePath, include.Span);
                    continue;
                }

                merged.Add(declaration is FunctionAst fn ? fn with { SourceFile = includePath } : declaration);
            }
        }

        return program with { Declarations = merged };
    }

    private static CompilationResult WithBinding(CompilationResult result, Binder binder) => result with
    {
        Capabilities = binder.Capabilities,
        GrantedEffects = binder.GrantedEffects,
        Readings = binder.Readings
    };

    public string SerializeIr(IrModule module) =>
        JsonSerializer.Serialize<IrNode>(module, IrJsonOptions);
}

/// <summary>
/// Serializes IR literal values without crashing on polymorphic object.
/// </summary>
file sealed class IrLiteralValueConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l,
            JsonTokenType.Number => reader.GetDecimal(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => JsonDocument.ParseValue(ref reader).RootElement.Clone()
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case decimal d:
                writer.WriteNumberValue(d);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
