using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class DynamicDataTests
{
    [Fact]
    public void Json_file_is_validated_into_records_and_summed()
    {
        var path = TestPaths.Sample(Path.Combine("data", "json-total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var body = result.Ir!.Functions[0].Body;
        Assert.IsType<IrCommand>(Assert.IsType<IrAssign>(body[0]).Value);
        Assert.IsType<IrDynamicType>(Assert.IsType<IrAssign>(body[1]).Value.Type);
        var validate = Assert.IsType<IrValidate>(Assert.IsType<IrAssign>(body[2]).Value);
        Assert.IsType<IrListType>(validate.Type);

        var input = System.Text.Json.JsonSerializer.Serialize(new { path = TestPaths.Sample(Path.Combine("data", "invoices.json")) });
        var exec = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "json-total", "Total", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(114.75m, Convert.ToDecimal(exec.Value));
        Assert.Contains("Read 3 invoices", exec.Output);
    }

    [Fact]
    public void Untyped_data_cannot_be_summed_without_validation()
    {
        var path = TestPaths.Sample(Path.Combine("errors", "untyped-data.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code.StartsWith("E2", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_field_fails_when_the_script_runs()
    {
        var path = TestPaths.Sample(Path.Combine("data", "json-total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        var broken = Path.Combine(Path.GetTempPath(), "writ-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(broken, """[{ "number": "A-1" }]""");
        try
        {
            var input = System.Text.Json.JsonSerializer.Serialize(new { path = broken });
            var exec = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "json-total", "Total", input);
            Assert.False(exec.Success, "the run should fail");
            Assert.Contains(exec.Errors, e => e.Contains("'amount'") && e.Contains("Invoice"));
        }
        finally
        {
            File.Delete(broken);
        }
    }

    [Fact]
    public void Rest_sample_checks_without_network()
    {
        var path = TestPaths.Sample(Path.Combine("data", "rest-total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Invoke-RestMethod -Uri $url", result.GeneratedPowerShell!);
    }

    [Fact]
    public void Interpolation_runs()
    {
        var source = """
            To Greet, given a name which is a string and a tally which is an integer, returning a string
                return "Hello {the name}, you have {the tally plus 1} items"
            """;
        var result = new CompilationPipeline().Compile(source, "greet.writ");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var input = """{ "name": "Ada", "tally": 2 }""";
        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "greet", "Greet", input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal("Hello Ada, you have 3 items", pwsh.Value);
    }
}
