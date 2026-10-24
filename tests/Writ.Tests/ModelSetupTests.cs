using System.Net;
using System.Text;
using System.Text.Json;
using Writ.Compiler.Compilation;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;

namespace Writ.Tests;

public class ModelSetupTests
{
    private const string Double = """
        To Double, given a value which is an integer, returning an integer
            return the value times 2
        """;

    private const string Priced = """
        An Item has price

        To Show, given item which is an Item, returning a string
            return the price of the item
        """;

    [Fact]
    public void A_project_without_a_model_file_uses_the_built_in_stand_in()
    {
        var result = new CompilationPipeline().Compile(Double, Path.Combine(Path.GetTempPath(), "no-model-file.writ"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("innate", result.Model!.Provider);
        Assert.Equal("built-in", result.Model.Source);
        Assert.Contains("model: innate (built-in)", Explainer.Explain(result, "no-model-file.writ"));
    }

    [Fact]
    public void The_sample_selects_the_innate_profile_from_its_project_file()
    {
        var path = TestPaths.Sample(Path.Combine("model", "double.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("innate", result.Model!.Provider);
        Assert.Equal("innate", result.Model.Profile);
        Assert.EndsWith("writ.model.json", result.Model.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_nearest_project_file_wins_over_a_parent_and_over_the_user_file()
    {
        var root = TempDir();
        try
        {
            var child = Path.Combine(root, "child");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(root, "writ.model.json"), """{ "provider": "openai", "model": "gpt-4.1-mini" }""");
            File.WriteAllText(Path.Combine(child, "writ.model.json"), """{ "provider": "innate" }""");
            var user = Path.Combine(root, "user.json");
            File.WriteAllText(user, """{ "provider": "openai", "model": "gpt-4.1-mini" }""");
            var path = Path.Combine(child, "double.writ");

            var result = new CompilationPipeline().Compile(Double, path, model: new ModelOptions
            {
                ReadUserFile = true,
                UserFilePath = user
            });
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(Path.GetFullPath(Path.Combine(child, "writ.model.json")), result.Model!.Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_user_file_applies_when_the_project_has_none()
    {
        var root = TempDir();
        try
        {
            var user = Path.Combine(root, "model.json");
            File.WriteAllText(user, """{ "provider": "innate" }""");
            var path = Path.Combine(root, "proj", "double.writ");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var result = new CompilationPipeline().Compile(Double, path, model: new ModelOptions
            {
                ReadUserFile = true,
                UserFilePath = user
            });
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(Path.GetFullPath(user), result.Model!.Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_named_profile_is_selected_with_model_options()
    {
        var handler = new StubHandler(_ => Reading("""{"id":"string","subject":"price","rationale":"the server said string"}"""));
        var result = Compile(Profiles(), Priced, new ModelOptions
        {
            Profile = "openai",
            Handler = handler,
            ReadEnvironment = name => name == "OPENAI_API_KEY" ? "test-key" : null
        });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("openai", result.Model!.Provider);
        Assert.Equal("gpt-4.1-mini", result.Model.Model);
        Assert.Equal("openai", result.Model.Profile);
        var record = Assert.IsType<IrRecordType>(result.Ir!.Types[0].Type);
        Assert.Equal("string", Assert.IsType<IrPrimitiveType>(record.Fields[0].Type).Name);
        Assert.Contains(""""model":"gpt-4.1-mini"""", handler.Body);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization.Parameter);
        Assert.EndsWith("/v1/chat/completions", handler.Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Ollama_defaults_to_the_local_server_and_sends_no_key()
    {
        var handler = new StubHandler(_ => Reading("""{"id":"string","subject":"price","rationale":"local"}"""));
        var result = Compile("""{ "provider": "ollama", "model": "qwen2.5-coder" }""", Priced, new ModelOptions { Handler = handler });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Null(handler.Request.Headers.Authorization);
        Assert.Contains(""""response_format":{"type":"json_object"}"""", handler.Body);
    }

    [Fact]
    public void Copilot_calls_the_copilot_chat_endpoint_with_its_integration_header()
    {
        var handler = new StubHandler(_ => Reading("""{"id":"string","subject":"price","rationale":"copilot"}"""));
        var result = Compile("""{ "provider": "github-copilot", "model": "gpt-4o" }""", Priced, new ModelOptions
        {
            Handler = handler,
            ReadEnvironment = name => name == "GITHUB_TOKEN" ? "gh-test" : null
        });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("https://api.githubcopilot.com/chat/completions", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("gh-test", handler.Request.Headers.Authorization!.Parameter);
        Assert.True(handler.Request.Headers.TryGetValues("Copilot-Integration-Id", out var values));
        Assert.Equal("copilot-developer-cli", Assert.Single(values));
    }

    [Fact]
    public void An_unknown_provider_a_secret_in_the_file_and_a_missing_key_are_errors()
    {
        AssertCode("""{ "provider": "anthropic", "model": "claude" }""", "E2107", "provider");
        AssertCode("""{ "provider": "openai", "model": "gpt-4.1-mini", "apiKey": "sk-secret" }""", "E2107", "apiKeyEnv");
        AssertCode("""{ "provider": "openai", "model": "gpt-4.1-mini" }""", "E2107", "OPENAI_API_KEY");
        AssertCode("""{ "provider": "compatible", "model": "local" }""", "E2107", "endpoint");
        AssertCode("""{ "use": "missing", "models": { "ollama": { "provider": "innate" } } }""", "E2107", "no model named");
        AssertCode("""{ "provider": "ollama", "model": "qwen" }""", "E2107", "does not match", profile: "openai");
    }

    [Fact]
    public void A_model_that_returns_prose_or_an_error_fails_the_reading()
    {
        var prose = Compile("""{ "provider": "ollama", "model": "qwen" }""", Priced, new ModelOptions
        {
            Handler = new StubHandler(_ => """{"choices":[{"message":{"content":"I think price is a decimal."}}]}""")
        });
        Assert.Contains(prose.Diagnostics, diagnostic => diagnostic.Code == "E2106" && diagnostic.Message.Contains("did not return a reading"));

        var denied = Compile("""{ "provider": "ollama", "model": "qwen" }""", Priced, new ModelOptions
        {
            Handler = new StubHandler(_ => """{"error":"no"}""", HttpStatusCode.Unauthorized)
        });
        Assert.Contains(denied.Diagnostics, diagnostic => diagnostic.Code == "E2106" && diagnostic.Message.Contains("401"));
    }

    [Fact]
    public void A_fenced_reply_with_a_numeric_binding_parses()
    {
        var reading = ModelMessages.Parse("""
            ```json
            {"selections":[{"id":"sumProjection","subject":"subtotal","bindings":{"left":"price","right":2},"rationale":"sum","order":1}],"failureReason":null}
            ```
            """);

        var selection = Assert.Single(reading.Selections);
        Assert.Equal("sumProjection", selection.Id);
        Assert.Equal("2", selection.Bindings["right"]);
        Assert.Equal(1, selection.Order);
    }

    [Fact]
    public void An_explicit_model_ignores_a_broken_project_file()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "writ.model.json"), """{ "provider": "nope" }""");
            var path = Path.Combine(dir, "double.writ");
            var result = new CompilationPipeline(model: new InnateLanguageModel()).Compile(Double, path);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Null(result.Model);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Passing_a_profile_with_no_file_is_an_error()
    {
        var bag = new DiagnosticBag();
        var resolved = ModelSetup.Resolve(Path.Combine(Path.GetTempPath(), "absent.writ"), new ModelOptions { Profile = "openai" }, bag);
        Assert.Null(resolved);
        Assert.Contains(bag.Items, diagnostic => diagnostic.Code == "E2107" && diagnostic.Message.Contains("--model"));
    }

    private static void AssertCode(string json, string code, string message, string? profile = null)
    {
        var result = Compile(json, Priced, new ModelOptions { Profile = profile, ReadEnvironment = _ => null });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    private static CompilationResult Compile(string json, string source, ModelOptions? model = null)
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "writ.model.json"), json);
            var path = Path.Combine(dir, "priced.writ");
            return new CompilationPipeline().Compile(source, path, model: model);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Profiles() => """
        {
          "use": "innate",
          "models": {
            "innate": { "provider": "innate" },
            "openai": { "provider": "openai", "model": "gpt-4.1-mini" }
          }
        }
        """;

    private static string Reading(string selection) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = "{\"selections\":[" + selection + "]}" } }
            }
        });

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "writ-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class StubHandler(Func<string, string> reply, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(reply(Body), Encoding.UTF8, "application/json")
            };
        }
    }
}
