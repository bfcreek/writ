using Writ.Compiler.Compilation;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;

namespace Writ.Tests;

public class SemanticResolverTests
{
    [Fact]
    public void Intent_sum_compiles_and_runs()
    {
        var path = SamplePath("intent-sum.writ");
        var source = File.ReadAllText(path);
        var result = new CompilationPipeline().Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        Assert.Contains(result.Ir!.Functions[0].Body, n => n is IrAssign { Value: IrSumProjection });

        var input = File.ReadAllText(SamplePath("input-calculate-total.json"));
        var exec = new RoslynExecutor().Execute(result.GeneratedCSharp!, "intent-sum", "CalculateTotal", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(27.54m, Assert.IsType<decimal>(exec.Value));
    }

    [Fact]
    public void Intent_filter_compiles_and_runs()
    {
        var path = SamplePath("intent-filter.writ");
        var source = File.ReadAllText(path);
        var result = new CompilationPipeline().Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(result.Ir!.Functions[0].Body, n => n is IrAssign { Value: IrFilter });

        var input = File.ReadAllText(SamplePath("input-intent-filter.json"));
        var exec = new RoslynExecutor().Execute(result.GeneratedCSharp!, "intent-filter", "PositiveQuantity", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        var list = Assert.IsAssignableFrom<System.Collections.IList>(exec.Value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Ambiguous_intent_fails_with_E2104()
    {
        var path = SamplePath(Path.Combine("errors", "intent-ambiguous.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E2104");
        Assert.Contains("isActive", diagnostic.Message);
        Assert.Contains("status", diagnostic.Message);
        Assert.True(diagnostic.Span.StartLine >= 1);
    }

    [Fact]
    public void Unresolved_intent_fails_with_E2103()
    {
        var path = SamplePath(Path.Combine("errors", "intent-unresolved.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E2103");
    }

    [Fact]
    public void Intent_with_unknown_field_fails_validation_E2105()
    {
        var path = SamplePath(Path.Combine("errors", "intent-bad-field.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E2105" && d.Message.Contains("price"));
    }

    [Fact]
    public void Invalid_scripted_proposal_is_rejected_by_lowerer()
    {
        var source = """
            An Item has
                a quantity which is an integer

            To Broken, given items which are a list of Item, returning a list of Item
                the keep is the items where the quantity is greater than 0
                return the keep
            """;

        var resolver = new ScriptedSemanticResolver(_ =>
            SemanticResolutionResult.Unique(new IntentProposalCandidate(
                new FilterProposal(
                    "items",
                    "item",
                    new ProposalBinary(new ProposalField("missingField"), ">", new ProposalLiteral(0L))),
                "bad proposal",
                [])));

        var result = new CompilationPipeline(resolver).Compile(source, "scripted.writ");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E2105" && d.Message.Contains("missingField"));
    }

    [Fact]
    public void Deterministic_code_still_compiles_without_resolver_calls()
    {
        var path = SamplePath("fizzbuzz.writ");
        var result = new CompilationPipeline(new StubSemanticResolver()).Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Fact]
    public void Resolved_ir_has_ResolvedByModel_provenance()
    {
        var path = SamplePath("intent-sum.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var assign = Assert.IsType<IrAssign>(result.Ir!.Functions[0].Body[0]);
        Assert.Equal(Provenance.ResolvedByModel, assign.Value.Provenance);
    }

    private static string SamplePath(string relative) =>
        Path.GetFullPath(Path.Combine(RepoRoot(), "samples", relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Writ.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}

/// <summary>Test helper that returns a fixed resolution for any phrase.</summary>
file sealed class ScriptedSemanticResolver(Func<SemanticResolutionRequest, SemanticResolutionResult> handler)
    : ISemanticResolver
{
    public Task<SemanticResolutionResult> ResolveAsync(
        SemanticResolutionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(handler(request));
}
