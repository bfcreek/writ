using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Syntax;

public abstract record AstNode(SourceSpan Span);

public sealed record ProgramAst(IReadOnlyList<AstNode> Declarations, SourceSpan Span) : AstNode(Span);

/// <summary>"Use payments from "payments.capability.json"" or "Use payments from mcp "pwsh -File server.ps1"".</summary>
public sealed record UseDeclAst(string Alias, string ManifestPath, SourceSpan Span, string SourceKind = "file") : AstNode(Span);

/// <summary>"Allow reading files and network access" — effect grants for cmdlets.</summary>
public sealed record AllowDeclAst(IReadOnlyList<string> Phrases, SourceSpan Span) : AstNode(Span);

public sealed record SayStmtAst(IReadOnlyList<ExprAst> Parts, SourceSpan Span) : StmtAst(Span);

/// <summary>A sentence that is a command on its own line, such as "remove the folder".</summary>
public sealed record ExpressionStmtAst(ExprAst Value, SourceSpan Span) : StmtAst(Span);

/// <summary>"try to … / if that fails … / finally …". Inside the failure block, "the error" is the message.</summary>
public sealed record TryStmtAst(
    IReadOnlyList<StmtAst> Body,
    IReadOnlyList<StmtAst>? OnFailure,
    IReadOnlyList<StmtAst>? Finally,
    SourceSpan Span) : StmtAst(Span);

public sealed record WhileStmtAst(ExprAst Condition, IReadOnlyList<StmtAst> Body, bool Until, SourceSpan Span) : StmtAst(Span);

/// <summary>"the files sorted by length, largest first"</summary>
public sealed record SortExprAst(ExprAst Target, string Field, bool Descending, SourceSpan Span) : ExprAst(Span);

/// <summary>"the data as a list of Invoice" — validate untyped data into a declared type.</summary>
public sealed record TreatExprAst(ExprAst Target, TypeAst Type, SourceSpan Span) : ExprAst(Span);

/// <summary>"the first 10 of the files"</summary>
public sealed record TakeExprAst(ExprAst Target, ExprAst Count, SourceSpan Span) : ExprAst(Span);

public sealed record TypeDeclAst(
    string Name,
    IReadOnlyList<FieldDeclAst> Fields,
    SourceSpan Span) : AstNode(Span)
{
    public bool IsEnum { get; init; }
    public IReadOnlyList<string> EnumMembers { get; init; } = [];
    public IReadOnlyList<FunctionAst> Methods { get; init; } = [];
}

public sealed record FieldDeclAst(string Name, TypeAst? Type, SourceSpan Span) : AstNode(Span);

public sealed record FunctionAst(
    string Name,
    IReadOnlyList<ParameterAst> Parameters,
    TypeAst ReturnType,
    IReadOnlyList<StmtAst> Body,
    SourceSpan Span,
    bool IsEntry = false,
    string? SourceFile = null) : AstNode(Span)
{
    /// <summary>"To filter Name" — a PowerShell filter.</summary>
    public bool IsFilter { get; init; }

    /// <summary>"To command Name" — [CmdletBinding()].</summary>
    public bool CmdletBinding { get; init; }
}

/// <summary>"Also use "helpers.writ"" — bring another file's types and functions into this program.</summary>
public sealed record IncludeDeclAst(string Path, SourceSpan Span) : AstNode(Span);

public sealed record ParameterAst(string Name, TypeAst Type, SourceSpan Span) : AstNode(Span)
{
    public ExprAst? Default { get; init; }
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
    public ExprAst? ValidateTest { get; init; }
}

/// <summary>"using the namespace System.Text" or "using the module "X"".</summary>
public sealed record UsingDeclAst(string Kind, string Name, SourceSpan Span) : AstNode(Span);

/// <summary>"export the function Greet".</summary>
public sealed record ExportDeclAst(string Name, SourceSpan Span) : AstNode(Span);

public abstract record TypeAst(SourceSpan Span) : AstNode(Span);

public sealed record NamedTypeAst(string Name, SourceSpan Span) : TypeAst(Span);

public sealed record ListTypeAst(TypeAst ElementType, SourceSpan Span) : TypeAst(Span);

public abstract record StmtAst(SourceSpan Span) : AstNode(Span);

public sealed record AssignStmtAst(string Name, ExprAst Value, SourceSpan Span) : StmtAst(Span)
{
    public string? Scope { get; init; }
    public string Operator { get; init; } = "=";
    /// <summary>"fixed" is ReadOnly, "locked" is Constant.</summary>
    public string? Option { get; init; }
}

public sealed record IfStmtAst(
    ExprAst Condition,
    IReadOnlyList<StmtAst> ThenBody,
    IReadOnlyList<StmtAst>? OtherwiseBody,
    SourceSpan Span) : StmtAst(Span);

public sealed record ForEachStmtAst(
    string Iterator,
    ExprAst Collection,
    IReadOnlyList<StmtAst> Body,
    SourceSpan Span) : StmtAst(Span);

public sealed record ReturnStmtAst(ExprAst Value, SourceSpan Span) : StmtAst(Span);

public abstract record ExprAst(SourceSpan Span) : AstNode(Span);

public sealed record LiteralExprAst(object? Value, SourceSpan Span) : ExprAst(Span);

public sealed record NameExprAst(string Name, SourceSpan Span, string? RawPhrase = null) : ExprAst(Span);

/// <summary>"the price of the item" or "Classify of n" — bound as a field or a call.</summary>
public sealed record OfExprAst(string Name, ExprAst Target, SourceSpan Span) : ExprAst(Span);

public sealed record CallExprAst(string Name, IReadOnlyList<ExprAst> Arguments, SourceSpan Span) : ExprAst(Span);

public sealed record FieldAccessExprAst(ExprAst Target, string FieldName, SourceSpan Span) : ExprAst(Span)
{
    public bool Optional { get; init; }
}

/// <summary>A flow, loop, switch, trap, data, or section statement from the PowerShell language.</summary>
public sealed record FlowStmtAst(string Kind, string? Label, ExprAst? Code, SourceSpan Span) : StmtAst(Span);

public sealed record LabeledStmtAst(string Label, StmtAst Statement, SourceSpan Span) : StmtAst(Span);

public sealed record DoStmtAst(IReadOnlyList<StmtAst> Body, ExprAst Condition, bool Until, SourceSpan Span) : StmtAst(Span);

public sealed record ForStmtAst(string Name, ExprAst From, ExprAst To, IReadOnlyList<StmtAst> Body, SourceSpan Span) : StmtAst(Span);

public sealed record SwitchArmAst(IReadOnlyList<ExprAst> Values, IReadOnlyList<StmtAst> Body, SourceSpan Span);

public sealed record SwitchStmtAst(
    ExprAst Value,
    IReadOnlyList<SwitchArmAst> Arms,
    IReadOnlyList<StmtAst>? Otherwise,
    SourceSpan Span) : StmtAst(Span);

public sealed record BlockStmtAst(string Kind, IReadOnlyList<StmtAst> Body, SourceSpan Span) : StmtAst(Span);

public sealed record RedirectStmtAst(string Operator, ExprAst Command, ExprAst? Target, SourceSpan Span) : StmtAst(Span);

public sealed record ModuleStmtAst(string Kind, string Name, SourceSpan Span) : StmtAst(Span);

/// <summary>An operator or form that is not one of the original arithmetic comparisons.</summary>
public sealed record ConstructExprAst(string Operator, IReadOnlyList<ExprAst> Arguments, SourceSpan Span) : ExprAst(Span);

public sealed record ListLiteralExprAst(IReadOnlyList<ExprAst> Elements, SourceSpan Span) : ExprAst(Span);

public sealed record UnaryExprAst(TokenKind Operator, ExprAst Operand, SourceSpan Span) : ExprAst(Span);

public sealed record BinaryExprAst(ExprAst Left, TokenKind Operator, ExprAst Right, SourceSpan Span) : ExprAst(Span);

/// <summary>
/// Bounded natural-language phrase resolved by <c>ISemanticResolver</c> (Milestone B).
/// </summary>
public sealed record IntentExprAst(string Phrase, SourceSpan Span, bool Grammatical = false) : ExprAst(Span);

public sealed record AskArgAst(string? Name, ExprAst Value, SourceSpan Span);

public sealed record AskExprAst(
    string Capability,
    string Operation,
    IReadOnlyList<AskArgAst> Arguments,
    SourceSpan Span,
    int Retries = 0) : ExprAst(Span);

/// <summary>"run "greet.ps1" with name set to "Ada"" or "run "double.writ" with the value".</summary>
public sealed record RunExprAst(string Path, IReadOnlyList<AskArgAst> Arguments, SourceSpan Span) : ExprAst(Span);
