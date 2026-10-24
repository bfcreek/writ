using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;

namespace Writ.Tests;

public class ModelReadingTests
{
    [Fact]
    public void Omitted_field_types_and_subtotal_compile_and_run()
    {
        var path = SamplePath("innate-total.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var record = Assert.IsType<IrRecordType>(result.Ir!.Types[0].Type);
        Assert.Equal("decimal", Assert.IsType<IrPrimitiveType>(record.Fields[0].Type).Name);
        Assert.Equal("integer", Assert.IsType<IrPrimitiveType>(record.Fields[1].Type).Name);
        Assert.Contains(result.Ir.Functions[0].Body, node => node is IrAssign { Value: IrBinary { Left: IrSumProjection } });

        var exec = new RoslynExecutor().Execute(
            result.GeneratedCSharp!,
            "innate-total",
            "CalculateTotal",
            File.ReadAllText(SamplePath("input-calculate-total.json")));
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(27.54m, Assert.IsType<decimal>(exec.Value));
    }

    [Fact]
    public void Explicit_type_overrides_innate_guess()
    {
        var result = new CompilationPipeline().Compile("""
            An InvoiceItem has
                a price which is a string
                and a quantity

            To Show, given item which is an InvoiceItem, returning a string
                return the price of the item
            """, "test.writ");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var record = Assert.IsType<IrRecordType>(result.Ir!.Types[0].Type);
        Assert.Equal("string", Assert.IsType<IrPrimitiveType>(record.Fields.Single(f => f.Name == "price").Type).Name);
        Assert.Equal("integer", Assert.IsType<IrPrimitiveType>(record.Fields.Single(f => f.Name == "quantity").Type).Name);
    }

    [Fact]
    public void Defined_subtotal_is_not_replaced()
    {
        var model = new RecordingModel(new InnateLanguageModel());
        var result = new CompilationPipeline(model: model).Compile("""
            An InvoiceItem has price and quantity

            To Fixed, given items which are a list of InvoiceItem, returning a decimal
                the subtotal is 4.0
                return the subtotal
            """, "test.writ");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.DoesNotContain(model.Texts, text => text.Equals("subtotal", StringComparison.OrdinalIgnoreCase));

        var exec = new RoslynExecutor().Execute(result.GeneratedCSharp!, "test", "Fixed", """{"items":[]}""");
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(4m, Assert.IsType<decimal>(exec.Value));
    }

    [Fact]
    public void Competing_subtotals_fail()
    {
        var result = new CompilationPipeline().Compile("""
            An InvoiceItem has price and quantity

            An Fee has price and quantity

            To Ambiguous, given items which are a list of InvoiceItem and fees which are a list of Fee, returning a decimal
                return the subtotal
            """, "test.writ");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E2104");
    }

    [Fact]
    public void Selection_outside_the_catalog_is_rejected()
    {
        var model = new ScriptedModel(_ => ModelReading.Of(
            new ModelSelection("currency", "price", new Dictionary<string, string>(), "Money is a currency.")));
        var result = new CompilationPipeline(model: model).Compile("""
            An InvoiceItem has price

            To Show, returning an integer
                return 1
            """, "test.writ");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E2105" && diagnostic.Message.Contains("currency"));
    }

    [Fact]
    public async Task But_first_runs_before_the_clause_that_was_spoken_first()
    {
        var operations = PaymentOperations();
        var symbols = new[]
        {
            new SymbolInfo("day", "string"),
            new SymbolInfo("paymentId", "string")
        };
        foreach (var phrase in new[]
        {
            "retry the payment, but first list the failures",
            "retry the payment, gut first list the failures"
        })
        {
            var reading = await new InnateLanguageModel().ReadAsync(Request(phrase, operations, symbols));
            Assert.Equal(["payments.list_failed", "payments.retry"], reading.Selections.Select(selection => selection.Id).ToArray());
            Assert.Equal([0, 1], reading.Selections.Select(selection => selection.Order).ToArray());
        }
    }

    [Fact]
    public async Task Then_keeps_spoken_order()
    {
        var operations = PaymentOperations();
        var symbols = new[] { new SymbolInfo("day", "string"), new SymbolInfo("paymentId", "string") };
        var reading = await new InnateLanguageModel().ReadAsync(
            Request("retry the payment then list the failures", operations, symbols));
        Assert.Equal(["payments.retry", "payments.list_failed"], reading.Selections.Select(selection => selection.Id).ToArray());
    }

    [Fact]
    public void Spoken_order_is_not_execution_order()
    {
        var path = SamplePath("but-first.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var sequence = result.Ir!.Functions[0].Body
            .Select(node => node is IrAssign assign ? assign.Value : null)
            .OfType<IrSequence>()
            .Single();
        Assert.Equal("list_failed", Assert.IsType<IrCapabilityCall>(sequence.Steps[0]).Operation);
        Assert.Equal("retry", Assert.IsType<IrCapabilityCall>(sequence.Steps[1]).Operation);

        var source = result.GeneratedCSharp!;
        Assert.True(source.IndexOf("\"list_failed\"", StringComparison.Ordinal) < source.IndexOf("\"retry\"", StringComparison.Ordinal));

        var exec = new RoslynExecutor().Execute(
            source,
            "but-first",
            "Recover",
            File.ReadAllText(SamplePath("input-but-first.json")));
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.True(Assert.IsType<bool>(exec.Value));
    }

    private static ModelRequest Request(string text, IReadOnlyList<CatalogEntry> operations, IReadOnlyList<SymbolInfo> symbols) =>
        new(text, [], operations, [], [], symbols, []);

    private static IReadOnlyList<CatalogEntry> PaymentOperations() =>
    [
        new("payments.list_failed", "payments list failed", ["day"]),
        new("payments.retry", "payments retry", ["paymentId"])
    ];

    [Fact]
    public async Task One_reading_can_name_several_catalog_entries()
    {
        var request = new ModelRequest(
            "InvoiceItem has price and quantity",
            LanguageCatalog.Types,
            LanguageCatalog.Operations,
            ["price", "quantity"],
            Rules: [],
            Symbols: [],
            Records: []);
        var reading = await new InnateLanguageModel().ReadAsync(request);
        var accepted = ModelReadings.Accept(reading, request, new Writ.Compiler.Diagnostics.DiagnosticBag(), null, default);
        Assert.NotNull(accepted);
        Assert.Equal(2, accepted.Count);
        Assert.Contains(accepted, selection => selection.Subject == "price" && selection.Id == "decimal");
        Assert.Contains(accepted, selection => selection.Subject == "quantity" && selection.Id == "integer");
    }

    private static string SamplePath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Writ.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "samples", relative);
    }

    private sealed class RecordingModel(ILanguageModel inner) : ILanguageModel
    {
        public List<string> Texts { get; } = [];

        public Task<ModelReading> ReadAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Texts.Add(request.Text);
            return inner.ReadAsync(request, cancellationToken);
        }
    }

    private sealed class ScriptedModel(Func<ModelRequest, ModelReading> read) : ILanguageModel
    {
        public Task<ModelReading> ReadAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(read(request));
    }
}
