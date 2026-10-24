using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Syntax;

public sealed record Token(TokenKind Kind, string Text, SourceSpan Span, object? Literal = null);
