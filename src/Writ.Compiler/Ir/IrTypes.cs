using System.Text.Json.Serialization;
using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Ir;

public static class IrVersions
{
    public const string SchemaVersion = "0.7.0";
}

public enum Provenance
{
    Deterministic,
    ResolvedByModel
}

public enum EffectKind
{
    Pure,
    FileSystemRead,
    FileSystemWrite,
    NetworkRead,
    NetworkWrite,
    ExternalState,
    RuntimeAi,
    /// <summary>Running a PowerShell script whose body the compiler cannot see.</summary>
    Script
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IrModule), "module")]
[JsonDerivedType(typeof(IrTypeDecl), "typeDecl")]
[JsonDerivedType(typeof(IrFunction), "function")]
[JsonDerivedType(typeof(IrAssign), "assign")]
[JsonDerivedType(typeof(IrIf), "if")]
[JsonDerivedType(typeof(IrForEach), "forEach")]
[JsonDerivedType(typeof(IrReturn), "return")]
[JsonDerivedType(typeof(IrLiteral), "literal")]
[JsonDerivedType(typeof(IrVar), "var")]
[JsonDerivedType(typeof(IrCall), "call")]
[JsonDerivedType(typeof(IrFieldAccess), "fieldAccess")]
[JsonDerivedType(typeof(IrListLiteral), "listLiteral")]
[JsonDerivedType(typeof(IrBinary), "binary")]
[JsonDerivedType(typeof(IrUnary), "unary")]
[JsonDerivedType(typeof(IrSumProjection), "sumProjection")]
[JsonDerivedType(typeof(IrFilter), "filter")]
[JsonDerivedType(typeof(IrCapabilityCall), "capabilityCall")]
[JsonDerivedType(typeof(IrSequence), "sequence")]
[JsonDerivedType(typeof(IrSort), "sort")]
[JsonDerivedType(typeof(IrTake), "take")]
[JsonDerivedType(typeof(IrSelectField), "selectField")]
[JsonDerivedType(typeof(IrSay), "say")]
[JsonDerivedType(typeof(IrWhile), "while")]
[JsonDerivedType(typeof(IrCommand), "command")]
[JsonDerivedType(typeof(IrValidate), "validate")]
[JsonDerivedType(typeof(IrTry), "try")]
[JsonDerivedType(typeof(IrRetry), "retry")]
[JsonDerivedType(typeof(IrScriptCall), "scriptCall")]
[JsonDerivedType(typeof(IrBreak), "break")]
[JsonDerivedType(typeof(IrContinue), "continue")]
[JsonDerivedType(typeof(IrExit), "exit")]
[JsonDerivedType(typeof(IrLabel), "label")]
[JsonDerivedType(typeof(IrDo), "do")]
[JsonDerivedType(typeof(IrFor), "for")]
[JsonDerivedType(typeof(IrSwitch), "switch")]
[JsonDerivedType(typeof(IrTrap), "trap")]
[JsonDerivedType(typeof(IrData), "data")]
[JsonDerivedType(typeof(IrSection), "section")]
[JsonDerivedType(typeof(IrRedirect), "redirect")]
[JsonDerivedType(typeof(IrDotSource), "dotSource")]
public abstract record IrNode(
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic);

public sealed record IrModule(
    string Name,
    string SchemaVersion,
    IReadOnlyList<IrTypeDecl> Types,
    IReadOnlyList<IrFunction> Functions,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    public IReadOnlyList<IrUsing> Usings { get; init; } = [];
    public IReadOnlyList<string> Exports { get; init; } = [];
}

/// <summary>"using the namespace …" or "using the module …".</summary>
public sealed record IrUsing(string Kind, string Name);

public sealed record IrTypeDecl(
    string Name,
    IReadOnlyList<IrField> Fields,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    public IReadOnlyList<string> EnumMembers { get; init; } = [];
    public IReadOnlyList<IrFunction> Methods { get; init; } = [];
}

public sealed record IrField(string Name, IrType Type);

public sealed record IrFunction(
    string Name,
    IReadOnlyList<IrParameter> Parameters,
    IrType ReturnType,
    IReadOnlyList<IrNode> Body,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    /// <summary>True for the "When run" block: it becomes the script's own parameter block and body.</summary>
    public bool IsEntry { get; init; }

    /// <summary>Set when the function came from an "Also use" file.</summary>
    public string? SourceFile { get; init; }

    public bool IsFilter { get; init; }
    public bool CmdletBinding { get; init; }
    public bool IsMethod { get; init; }
}

public sealed record IrParameter(string Name, IrType Type)
{
    public IrNode? Default { get; init; }
    public bool FromPipeline { get; init; }
    public string? Alias { get; init; }
    public IReadOnlyList<string>? OneOf { get; init; }
    public string? Pattern { get; init; }
    public long? Min { get; init; }
    public long? Max { get; init; }
    public long? MinLength { get; init; }
    public long? MaxLength { get; init; }
    public long? MinCount { get; init; }
    public long? MaxCount { get; init; }
    public bool AllowNull { get; init; }
    public bool AllowEmptyString { get; init; }
    public bool AllowEmptyCollection { get; init; }
    public bool SupportsWildcards { get; init; }
    public bool RejectNull { get; init; }
    public bool RejectEmpty { get; init; }
    public IrNode? ValidateTest { get; init; }
}

public sealed record IrAssign(
    string Name,
    IrNode Value,
    bool IsDeclaration,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    public string? Scope { get; init; }
    public string Operator { get; init; } = "=";
    public string? Option { get; init; }
}

public sealed record IrIf(
    IrNode Condition,
    IReadOnlyList<IrNode> ThenBody,
    IReadOnlyList<IrNode>? OtherwiseBody,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrForEach(
    string Iterator,
    IrNode Collection,
    IReadOnlyList<IrNode> Body,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrReturn(
    IrNode Value,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrLiteral(
    object? Value,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrVar(
    string Name,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    public string? Scope { get; init; }
}

public sealed record IrCall(
    string Callee,
    IReadOnlyList<IrNode> Arguments,
    bool IsBuiltin,
    IReadOnlyList<EffectKind> Effects,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrFieldAccess(
    IrNode Target,
    string FieldName,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    public bool Optional { get; init; }
}

public sealed record IrListLiteral(
    IReadOnlyList<IrNode> Elements,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrBinary(
    IrNode Left,
    string Operator,
    IrNode Right,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrUnary(
    string Operator,
    IrNode Operand,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

/// <summary>Milestone B: sum of a projection over a collection (resolved intent).</summary>
public sealed record IrSumProjection(
    IrNode Collection,
    string Iterator,
    IrNode Projection,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.ResolvedByModel) : IrNode(Span, Type, Provenance);

/// <summary>Milestone B: filter a collection by predicate (resolved intent).</summary>
public sealed record IrFilter(
    IrNode Collection,
    string Iterator,
    IrNode Predicate,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.ResolvedByModel) : IrNode(Span, Type, Provenance);

public sealed record IrNamedArgument(string Name, IrNode Value);

/// <summary>A bound external capability call. Compilation never invokes it.</summary>
public sealed record IrCapabilityCall(
    string Alias,
    string Operation,
    string Binding,
    string ManifestPath,
    IReadOnlyList<IrNamedArgument> Arguments,
    IReadOnlyList<EffectKind> Effects,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    /// <summary>mock | mcp | powershell-module | http — decides how the runtime routes the call.</summary>
    public string Provider { get; init; } = "mock";

    /// <summary>MCP command line, module name, or base URL. Null for mock.</summary>
    public string? Endpoint { get; init; }

    /// <summary>stdio or rest. REST is an http or https endpoint.</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>Directory the endpoint's relative paths are resolved against at run time.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// Operations in execution order. Later tool commands, including MCP, use this same shape.
/// </summary>
public sealed record IrSequence(
    IReadOnlyList<IrNode> Steps,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.ResolvedByModel) : IrNode(Span, Type, Provenance);

public sealed record IrSort(
    IrNode Collection,
    string Field,
    bool Descending,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrTake(
    IrNode Collection,
    IrNode Count,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrSelectField(
    IrNode Collection,
    string Field,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrSay(
    IReadOnlyList<IrNode> Parts,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrWhile(
    IrNode Condition,
    IReadOnlyList<IrNode> Body,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

/// <summary>try / catch / finally. Inside <see cref="OnFailure"/>, <see cref="ErrorName"/> holds the message.</summary>
public sealed record IrTry(
    IReadOnlyList<IrNode> Body,
    IReadOnlyList<IrNode>? OnFailure,
    IReadOnlyList<IrNode>? Finally,
    string ErrorName,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

/// <summary>Re-runs an idempotent operation up to <see cref="Times"/> times before giving up.</summary>
public sealed record IrRetry(
    IrNode Operation,
    int Times,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

/// <summary>Checks untyped data against a declared type when the program runs. Fails loudly on a missing field.</summary>
public sealed record IrValidate(
    IrNode Value,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

/// <summary>
/// A call to another script. <see cref="Kind"/> is "writ" or "powershell".
/// Compilation reads the contract and never runs the script.
/// </summary>
public sealed record IrScriptCall(
    string Path,
    string Kind,
    IReadOnlyList<IrNamedArgument> Arguments,
    IReadOnlyList<EffectKind> Effects,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance)
{
    /// <summary>Generated PowerShell for a Writ script. A .ps1 is invoked from <see cref="Path"/>.</summary>
    public string? CompiledScript { get; init; }
}

/// <summary>A bound PowerShell command (cmdlet) from the pinned catalog. Compilation never runs it.</summary>
public sealed record IrBreak(
    string? Label,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrContinue(
    string? Label,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrExit(
    IrNode? Code,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrLabel(
    string Name,
    IrNode Statement,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrDo(
    IrNode Condition,
    IReadOnlyList<IrNode> Body,
    bool Until,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrFor(
    string Name,
    IrNode From,
    IrNode To,
    IReadOnlyList<IrNode> Body,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrSwitchArm(IReadOnlyList<IrNode> Values, IReadOnlyList<IrNode> Body);

public sealed record IrSwitch(
    IrNode Value,
    IReadOnlyList<IrSwitchArm> Arms,
    IReadOnlyList<IrNode>? Otherwise,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrTrap(
    IReadOnlyList<IrNode> Body,
    string ErrorName,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrData(
    IReadOnlyList<IrNode> Body,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrSection(
    string Name,
    IReadOnlyList<IrNode> Body,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrRedirect(
    string Operator,
    IrNode Command,
    IrNode? Target,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrDotSource(
    IrNode Path,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.Deterministic) : IrNode(Span, Type, Provenance);

public sealed record IrCommand(
    string Name,
    IReadOnlyList<IrNamedArgument> Arguments,
    IReadOnlyList<EffectKind> Effects,
    string Binding,
    SourceSpan Span,
    IrType Type,
    Provenance Provenance = Provenance.ResolvedByModel) : IrNode(Span, Type, Provenance);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "typeKind")]
[JsonDerivedType(typeof(IrPrimitiveType), "primitive")]
[JsonDerivedType(typeof(IrListType), "list")]
[JsonDerivedType(typeof(IrRecordType), "record")]
[JsonDerivedType(typeof(IrVoidType), "void")]
[JsonDerivedType(typeof(IrErrorType), "error")]
[JsonDerivedType(typeof(IrDynamicType), "dynamic")]
public abstract record IrType;

/// <summary>Untyped data from a command or document. Typed use requires a validation step.</summary>
public sealed record IrDynamicType : IrType
{
    public static readonly IrDynamicType Instance = new();
}

public sealed record IrPrimitiveType(string Name) : IrType
{
    public static readonly IrPrimitiveType Integer = new("integer");
    public static readonly IrPrimitiveType Decimal = new("decimal");
    public static readonly IrPrimitiveType String = new("string");
    public static readonly IrPrimitiveType Boolean = new("boolean");
    public static readonly IrPrimitiveType Null = new("null");
    public static readonly IrPrimitiveType Character = new("character");
    public static readonly IrPrimitiveType Float = new("float");
    public static readonly IrPrimitiveType Table = new("table");
    public static readonly IrPrimitiveType Pattern = new("pattern");
    public static readonly IrPrimitiveType Xml = new("xml");
    public static readonly IrPrimitiveType Block = new("block");
    public static readonly IrPrimitiveType Object = new("object");
    public static readonly IrPrimitiveType Switch = new("switch");
}

public sealed record IrListType(IrType ElementType) : IrType;

public sealed record IrRecordType(string Name, IReadOnlyList<IrField> Fields) : IrType;

public sealed record IrVoidType : IrType
{
    public static readonly IrVoidType Instance = new();
}

public sealed record IrErrorType : IrType
{
    public static readonly IrErrorType Instance = new();
}
