namespace Writ.Compiler.Diagnostics;

public readonly record struct SourceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public static SourceSpan FromToken(int line, int column, int length) =>
        new(line, column, line, column + Math.Max(length, 1) - 1);

    public override string ToString() => $"{StartLine}:{StartColumn}";
}
