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
    SourceSpan Span) : AstNode(Span);

public sealed record FieldDeclAst(string Name, TypeAst? Type, SourceSpan Span) : AstNode(Span);

public sealed record FunctionAst(
    string Name,
    IReadOnlyList<ParameterAst> Parameters,
    TypeAst ReturnType,
    IReadOnlyList<StmtAst> Body,
    SourceSpan Span,
    bool IsEntry = false,
    string? SourceFile = null) : AstNode(Span);

/// <summary>"Also use "helpers.writ"" — bring another file's types and functions into this program.</summary>
public sealed record IncludeDeclAst(string Path, SourceSpan Span) : AstNode(Span);

public sealed record ParameterAst(string Name, TypeAst Type, SourceSpan Span) : AstNode(Span);

public abstract record TypeAst(SourceSpan Span) : AstNode(Span);

public sealed record NamedTypeAst(string Name, SourceSpan Span) : TypeAst(Span);

public sealed record ListTypeAst(TypeAst ElementType, SourceSpan Span) : TypeAst(Span);

public abstract record StmtAst(SourceSpan Span) : AstNode(Span);

public sealed record AssignStmtAst(string Name, ExprAst Value, SourceSpan Span) : StmtAst(Span);

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

public sealed record FieldAccessExprAst(ExprAst Target, string FieldName, SourceSpan Span) : ExprAst(Span);

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
