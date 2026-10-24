namespace Writ.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? FilePath,
    SourceSpan Span)
{
    public override string ToString()
    {
        var location = FilePath is null
            ? Span.ToString()
            : $"{FilePath}:{Span.StartLine}:{Span.StartColumn}";
        return $"{Severity.ToString().ToUpperInvariant()} {Code} {location}: {Message}";
    }
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Items => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Error(string code, string message, string? filePath, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, filePath, span));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}
