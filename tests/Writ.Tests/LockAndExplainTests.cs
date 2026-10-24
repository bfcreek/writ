using Writ.Compiler.Compilation;

namespace Writ.Tests;

public class LockAndExplainTests
{
    [Fact]
    public void Lock_records_model_readings_and_frozen_catches_a_changed_reading()
    {
        var path = TestPaths.Sample("folder-report.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var locked = LockFile.Build(source, result, pipeline.Cmdlets);
        Assert.Contains(locked.Readings, r => r.Selections.Any(s => s.Contains("pwsh.Get-ChildItem")));
        Assert.Empty(LockFile.Verify(locked, locked, path, frozen: true));

        // Same text hash, different reading: a frozen build must refuse.
        var drifted = new WritLock
        {
            SourceHash = locked.SourceHash,
            CompilerVersion = locked.CompilerVersion,
            IrSchema = locked.IrSchema,
            CmdletCatalogHash = locked.CmdletCatalogHash,
            Capabilities = locked.Capabilities,
            Readings = locked.Readings.Select(r => new LockedReading { Where = r.Where, Text = r.Text, Selections = ["0:pwsh.Get-Item"] }).ToList()
        };
        var diagnostics = LockFile.Verify(drifted, locked, path, frozen: true);
        Assert.Contains(diagnostics, d => d.Code == "E4009" && d.Message.Contains("model read the program differently"));
        Assert.Empty(LockFile.Verify(drifted, locked, path, frozen: false));
    }

    [Fact]
    public void Lock_roundtrips_through_disk()
    {
        var path = TestPaths.Sample("innate-total.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(source, path);
        var built = LockFile.Build(source, result, pipeline.Cmdlets);

        var temp = Path.Combine(Path.GetTempPath(), "writ-" + Guid.NewGuid().ToString("N") + ".lock.json");
        try
        {
            LockFile.Write(temp, built);
            var read = LockFile.Read(temp);
            Assert.NotNull(read);
            Assert.Empty(LockFile.Verify(read, built, path, frozen: true));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Explain_names_tools_effects_and_readings()
    {
        var path = TestPaths.Sample("careful-retry.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        var text = Explainer.Explain(result, path);
        Assert.Contains("tool payments: mock", text);
        Assert.Contains("retry up to 3 times", text);
        Assert.Contains("tool payments.retry(paymentId) via mock [changes something outside]", text);
        Assert.Contains("try / if that fails / finally", text);
    }

    [Fact]
    public void Diff_separates_text_edits_from_semantic_changes()
    {
        var path = TestPaths.Sample("innate-total.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var locked = LockFile.Build(source, pipeline.Compile(source, path), pipeline.Cmdlets);

        var edited = source + "\n# a comment\n";
        var current = LockFile.Build(edited, pipeline.Compile(edited, path), pipeline.Cmdlets);
        Assert.Contains("binds the same way", Explainer.Diff(locked, current));

        var retyped = source.Replace("price", "cost");
        var changed = LockFile.Build(retyped, pipeline.Compile(retyped, path), pipeline.Cmdlets);
        var diff = Explainer.Diff(locked, changed);
        Assert.Contains("+ reading", diff);
        Assert.Contains("- reading", diff);
    }
}
