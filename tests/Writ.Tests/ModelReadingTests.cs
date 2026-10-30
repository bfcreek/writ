using Writ.Compiler.Capabilities;
using Writ.Compiler.Compilation;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;

namespace Writ.Tests;

public class ModelReadingTests
{
    [Fact]
    public void Omitted_field_types_and_subtotal_compile_and_run()
    {
        var path = SamplePath(Path.Combine("basics", "innate-total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var record = Assert.IsType<IrRecordType>(result.Ir!.Types[0].Type);
        Assert.Equal("decimal", Assert.IsType<IrPrimitiveType>(record.Fields[0].Type).Name);
        Assert.Equal("integer", Assert.IsType<IrPrimitiveType>(record.Fields[1].Type).Name);
        Assert.Contains(result.Ir.Functions[0].Body, node => node is IrAssign { Value: IrBinary { Left: IrSumProjection } });

        var exec = new MachineExecutor().Execute(
            result.GeneratedCSharp!,
            "CalculateTotal",
            File.ReadAllText(SamplePath(Path.Combine("basics", "input-calculate-total.json"))));
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

        var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, "Fixed", """{"items":[]}""");
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
    public void Debug_trace_is_reached_for_a_file_with_no_trailing_newline()
    {
        var writer = new StringWriter();
        var result = new CompilationPipeline().Compile(
            "When run\n    list the files here",
            Path.Combine(Path.GetTempPath(), "hello-no-newline.writ"),
            model: new ModelOptions { Debug = writer });

        var trace = writer.ToString();
        Assert.Contains("debug: template read", trace);
        Assert.Contains("list the files here", trace);
        Assert.False(result.Success);
    }

    [Fact]
    public void Debug_trace_records_one_type_reading_and_a_rule_that_skipped_the_model()
    {
        var writer = new StringWriter();
        var result = new CompilationPipeline().Compile("""
            An InvoiceItem has price and quantity

            To Total, given items which are a list of InvoiceItem, returning a decimal
                return the sum of price times quantity for each item in items
            """, Path.Combine(Path.GetTempPath(), "debug-trace.writ"), model: new ModelOptions { Debug = writer });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var trace = writer.ToString();
        Assert.Equal(1, CountOf(trace, "debug: template read"));
        Assert.Contains("text: InvoiceItem has price and quantity", trace);
        Assert.Contains("decimal", trace);
        Assert.Contains("integer", trace);
        Assert.Contains("accepted", trace);
        Assert.Contains("debug: rule", trace);
        Assert.Contains("rule match: Sum of price * quantity over items.", trace);
        Assert.DoesNotContain("chat:", trace);
    }

    [Fact]
    public void Debug_trace_does_not_send_competing_rules_to_the_model()
    {
        var writer = new StringWriter();
        var path = SamplePath(Path.Combine("errors", "intent-ambiguous.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path, model: new ModelOptions { Debug = writer });

        Assert.False(result.Success);
        var trace = writer.ToString();
        Assert.Contains("2 rule matches; not read as a template", trace);
        Assert.DoesNotContain("debug: template read", trace);
    }

    [Fact]
    public void Debug_trace_marks_a_catalog_miss_as_rejected()
    {
        var writer = new StringWriter();
        var model = new ScriptedModel(_ => ModelReading.Of(
            new ModelSelection("currency", "price", new Dictionary<string, string>(), "Money is a currency.")));
        var result = new CompilationPipeline(model: model).Compile("""
            An InvoiceItem has price

            To Show, returning an integer
                return 1
            """, "test.writ", model: new ModelOptions { Debug = writer });

        Assert.False(result.Success);
        var trace = writer.ToString();
        Assert.Contains("0: currency (price) — Money is a currency.", trace);
        Assert.Contains("rejected", trace);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
        var path = SamplePath(Path.Combine("payments", "but-first.writ"));
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

        var exec = new MachineExecutor().Execute(
            source,
            "Recover",
            File.ReadAllText(SamplePath(Path.Combine("payments", "input-but-first.json"))));
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.True(Assert.IsType<bool>(exec.Value));
    }

    [Fact]
    public async Task Reordered_phrase_uses_the_nearest_catalog_template()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var reading = await new InnateLanguageModel().ReadAsync(
            Request("the file contents", operations, [new SymbolInfo("file", "string")]));
        Assert.Equal(["pwsh.Get-Content"], reading.Selections.Select(selection => selection.Id).ToArray());
        Assert.Equal("file", reading.Selections[0].Bindings["Path"]);
    }

    [Fact]
    public async Task Moved_words_keep_service_and_process_apart()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var reading = await new InnateLanguageModel().ReadAsync(
            Request("the service should stop", operations, [new SymbolInfo("service", "string")]));
        Assert.Equal(["pwsh.Stop-Service"], reading.Selections.Select(selection => selection.Id).ToArray());
    }

    [Fact]
    public async Task Two_equally_specific_templates_are_rejected()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var request = Request("the service process should stop", operations, []);
        var reading = await new InnateLanguageModel().ReadAsync(request);
        var bag = new DiagnosticBag();
        Assert.Null(ModelReadings.Accept(reading, request, bag, null, default));
        Assert.Contains(bag.Items, diagnostic => diagnostic.Code == "E2104");
    }

    [Fact]
    public async Task A_mentioned_name_fills_the_next_open_parameter()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var reading = await new InnateLanguageModel().ReadAsync(
            Request("unzip the archive into the unpacked", operations,
            [new SymbolInfo("folder", "string"), new SymbolInfo("archive", "string"), new SymbolInfo("unpacked", "string")]));
        var selection = Assert.Single(reading.Selections);
        Assert.Equal("pwsh.Expand-Archive", selection.Id);
        Assert.Equal("archive", selection.Bindings["Path"]);
        Assert.Equal("unpacked", selection.Bindings["DestinationPath"]);
    }

    [Fact]
    public async Task An_alias_in_scope_stands_in_only_when_the_sentence_names_nothing()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var model = new InnateLanguageModel();
        var symbols = new[] { new SymbolInfo("folder", "string"), new SymbolInfo("restored", "string") };

        var named = await model.ReadAsync(Request("the restored exists", operations, symbols));
        Assert.Equal("restored", Assert.Single(named.Selections).Bindings["Path"]);

        var implied = await model.ReadAsync(Request("list the files", operations, symbols));
        Assert.Equal("folder", Assert.Single(implied.Selections).Bindings["Path"]);
    }

    [Fact]
    public async Task A_switch_word_is_not_read_as_a_name()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var reading = await new InnateLanguageModel().ReadAsync(
            Request("the items in the folder, files only", operations,
            [new SymbolInfo("folder", "string"), new SymbolInfo("files", "list<dynamic>")]));
        var selection = Assert.Single(reading.Selections);
        Assert.Equal("pwsh.Get-ChildItem", selection.Id);
        Assert.Equal("folder", selection.Bindings["Path"]);
        Assert.Equal("true", selection.Bindings["File"]);
        Assert.False(selection.Bindings.ContainsKey("Filter"));
    }

    [Fact]
    public async Task A_name_without_its_article_still_matches_the_phrase()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var reading = await new InnateLanguageModel().ReadAsync(Request("uptime", operations, []));
        Assert.Equal("pwsh.Get-Uptime", Assert.Single(reading.Selections).Id);
    }

    [Fact]
    public async Task An_unrelated_sentence_matches_no_template()
    {
        var operations = CmdletCatalog.Default.AsCatalogEntries().ToList();
        var reading = await new InnateLanguageModel().ReadAsync(Request("wave hello there", operations, []));
        Assert.Empty(reading.Selections);
    }

    [Fact]
    public void File_contents_compiles_to_get_content()
    {
        var result = new CompilationPipeline().Compile("""
            Allow reading files
            When run, given a file which is a string, returning a string
                return the file contents
            """, "file-contents.writ");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Get-Content", result.GeneratedCSharp);
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
