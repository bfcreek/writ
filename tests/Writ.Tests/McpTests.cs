using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Writ.Compiler.Capabilities;
using Writ.Compiler.Codegen;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

[Collection("mcp")]
public class McpTests
{
    private static string ServerCommand =>
        $"pwsh -NoProfile -NonInteractive -File \"{Path.Combine(TestPaths.RepoRoot(), "tools", "mcp-test-server.ps1")}\"";

    [Fact]
    public void Discovery_lists_tools_and_hashes_them_stably()
    {
        var first = McpDiscovery.ListTools(ServerCommand, TestPaths.RepoRoot());
        var second = McpDiscovery.ListTools(ServerCommand, TestPaths.RepoRoot());
        Assert.Equal(["list_failed", "retry"], first.Tools.Select(t => t.Name).ToArray());
        Assert.True(first.Tools[0].ReadOnly);
        Assert.False(first.Tools[1].ReadOnly);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void Check_discovers_but_never_calls_a_tool()
    {
        var log = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N") + ".log");
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        try
        {
            var path = TestPaths.Sample("mcp-payments.writ");
            var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

            var methods = File.ReadAllLines(log);
            Assert.Contains("tools/list", methods);
            Assert.DoesNotContain("tools/call", methods);

            var capability = Assert.Single(result.Capabilities);
            Assert.Equal("mcp", capability.Provider);
            Assert.Equal(IrDynamicType.Instance, capability.Operations["list_failed"].ResultType);
            Assert.Contains("-Provider 'mcp'", result.GeneratedPowerShell!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            File.Delete(log);
        }
    }

    [Fact]
    public void Direct_mcp_program_runs()
    {
        var path = TestPaths.Sample("mcp-payments.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var input = """{ "day": "2026-10-20" }""";

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "mcp-payments", "RecoverFailures", input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal(1L, Convert.ToInt64(pwsh.Value));
        Assert.Equal(2, pwsh.CapabilityCalls);
    }

    [Fact]
    public void Manifest_backed_mcp_is_typed_and_runs()
    {
        var path = TestPaths.Sample("recover-failures-mcp.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal("mcp", capability.Provider);
        Assert.IsType<IrListType>(capability.Operations["list_failed"].ResultType);

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "recover-mcp", "RecoverFailures", """{ "day": "2026-10-20" }""");
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal(1L, Convert.ToInt64(pwsh.Value));
    }

    [Fact]
    public void Manifest_naming_a_missing_tool_is_rejected_at_check()
    {
        var folder = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var manifest = File.ReadAllText(TestPaths.Sample("payments-mcp.capability.json"))
                .Replace("../tools/mcp-test-server.ps1", Path.Combine(TestPaths.RepoRoot(), "tools", "mcp-test-server.ps1").Replace('\\', '/'))
                .Replace("\"name\": \"retry\"", "\"name\": \"refund\"")
                .Replace("\"list_failed\", \"retry\"", "\"list_failed\", \"refund\"");
            File.WriteAllText(Path.Combine(folder, "p.capability.json"), manifest);
            var source = """
                Use payments from "p.capability.json"

                To Tally, given a day which is a string, returning an integer
                    the failures are ask payments to list_failed with the day
                    return the count of the failures
                """;
            var result = new CompilationPipeline().Compile(source, Path.Combine(folder, "p.writ"));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "E4001" && d.Message.Contains("'refund'"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Lockfile_pins_the_tool_list_and_flags_drift()
    {
        var path = TestPaths.Sample("mcp-payments.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var current = LockFile.Build(source, result, pipeline.Cmdlets);
        Assert.Empty(LockFile.Verify(null, current, path, frozen: false));
        Assert.Empty(LockFile.Verify(current, current, path, frozen: true));

        var drifted = new WritLock
        {
            SourceHash = current.SourceHash,
            CompilerVersion = current.CompilerVersion,
            IrSchema = current.IrSchema,
            CmdletCatalogHash = current.CmdletCatalogHash,
            Capabilities = new Dictionary<string, LockedCapability>
            {
                ["payments"] = new() { Source = current.Capabilities["payments"].Source, Provider = "mcp", Hash = "sha256:old" }
            }
        };
        var diagnostics = LockFile.Verify(drifted, current, path, frozen: false);
        Assert.Contains(diagnostics, d => d.Code == "E4008");
    }

    [Fact]
    public void Mcp_tool_without_a_grant_is_rejected()
    {
        var source = $$"""
            Use payments from mcp "pwsh -NoProfile -NonInteractive -File ../tools/mcp-test-server.ps1"

            To Tally, given a day which is a string, returning an integer
                the failures are ask payments to list_failed with the day
                return 0
            """;
        var result = new CompilationPipeline().Compile(source, Path.Combine(TestPaths.RepoRoot(), "samples", "virtual.writ"));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E4003" && d.Message.Contains("Allow network access"));
    }

    [Fact]
    public void Defaults_file_discovers_tools_without_a_use_line()
    {
        var log = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N") + ".log");
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        try
        {
            var path = TestPaths.Sample(Path.Combine("shared", "recover.writ"));
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("Use payments", source);
            var result = new CompilationPipeline().Compile(source, path);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

            var methods = File.ReadAllLines(log);
            Assert.Contains("tools/list", methods);
            Assert.DoesNotContain("tools/call", methods);
            var capability = Assert.Single(result.Capabilities);
            Assert.Equal("payments", capability.Alias);
            Assert.Equal("mcp", capability.Provider);

            var ran = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "recover", "RecoverFailures", """{ "day": "2026-10-20" }""");
            Assert.True(ran.Success, string.Join(Environment.NewLine, ran.Errors));
            Assert.Equal(1L, Convert.ToInt64(ran.Value));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            if (File.Exists(log))
            {
                File.Delete(log);
            }
        }
    }

    [Fact]
    public void Explicit_use_wins_over_the_defaults_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var log = Path.Combine(folder, "mcp.log");
        var server = Path.Combine(TestPaths.RepoRoot(), "tools", "mcp-test-server.ps1").Replace('\\', '/');
        var command = $"pwsh -NoProfile -NonInteractive -File {server}";
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        try
        {
            File.WriteAllText(Path.Combine(folder, "writ.mcp.json"), $$"""
                { "servers": [ { "alias": "payments", "command": "{{command}}" } ] }
                """);
            var source = $$"""
                Use payments from mcp "{{command}}"
                Allow network access

                To Tally, given a day which is a string, returning an integer
                    the failures are ask payments to list_failed with the day
                    return the count of the failures
                """;
            var result = new CompilationPipeline().Compile(source, Path.Combine(folder, "tally.writ"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Single(result.Capabilities);
            Assert.Equal(1, File.ReadAllLines(log).Count(line => line == "tools/list"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Closer_file_wins_and_unused_servers_stay_stopped()
    {
        var root = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(user);
        var log = Path.Combine(root, "mcp.log");
        var markerParent = Path.Combine(root, "parent.txt");
        var markerUser = Path.Combine(root, "user.txt");
        var markerOther = Path.Combine(root, "other.txt");
        var server = Path.Combine(TestPaths.RepoRoot(), "tools", "mcp-test-server.ps1").Replace('\\', '/');
        var good = $"pwsh -NoProfile -NonInteractive -File {server}";
        File.WriteAllText(Path.Combine(root, "writ.mcp.json"), Servers(("payments", Touch(markerParent))));
        File.WriteAllText(Path.Combine(child, "writ.mcp.json"), Servers(("payments", good)));
        File.WriteAllText(Path.Combine(user, "writ.mcp.json"), Servers(("payments", Touch(markerUser)), ("other", Touch(markerOther))));
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        using var restore = McpDefaults.OverrideUserFile(Path.Combine(user, "writ.mcp.json"));
        try
        {
            var result = new CompilationPipeline().Compile(Tally, Path.Combine(child, "tally.writ"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var capability = Assert.Single(result.Capabilities);
            Assert.Equal(good, capability.Endpoint);
            Assert.Equal("stdio", capability.Transport);
            Assert.Equal(Path.GetFullPath(child), Path.GetFullPath(capability.WorkingDirectory!));
            Assert.Equal(1, File.ReadAllLines(log).Count(line => line == "tools/list"));
            Assert.False(File.Exists(markerParent));
            Assert.False(File.Exists(markerUser));
            Assert.False(File.Exists(markerOther));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            restore.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void User_file_supplies_an_alias_the_project_does_not()
    {
        var folder = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(folder, "user");
        Directory.CreateDirectory(user);
        var log = Path.Combine(folder, "mcp.log");
        var server = Path.Combine(TestPaths.RepoRoot(), "tools", "mcp-test-server.ps1").Replace('\\', '/');
        var command = $"pwsh -NoProfile -NonInteractive -File {server}";
        File.WriteAllText(Path.Combine(user, "writ.mcp.json"), Servers(("payments", command)));
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        using var restore = McpDefaults.OverrideUserFile(Path.Combine(user, "writ.mcp.json"));
        try
        {
            var result = new CompilationPipeline().Compile(Tally, Path.Combine(folder, "tally.writ"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var capability = Assert.Single(result.Capabilities);
            Assert.Equal(command, capability.Endpoint);
            Assert.Equal("stdio", capability.Transport);
            Assert.Equal(Path.GetFullPath(user), Path.GetFullPath(capability.WorkingDirectory!));
            Assert.Contains("tools/list", File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            restore.Dispose();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Invalid_defaults_file_names_itself()
    {
        var folder = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "writ.mcp.json");
        File.WriteAllText(path, "{");
        using var restore = McpDefaults.OverrideUserFile("");
        try
        {
            var result = new CompilationPipeline().Compile("""
                When run
                    say "hi"
                """, Path.Combine(folder, "hi.writ"));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "E4001" && d.Message.Contains(Path.GetFullPath(path)));
        }
        finally
        {
            restore.Dispose();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ListTools_prints_referenced_tools_and_does_not_call_them()
    {
        using var restore = McpDefaults.OverrideUserFile("");
        var path = TestPaths.Sample(Path.Combine("shared", "recover.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(".PARAMETER ListTools", result.GeneratedPowerShell);

        var temp = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var log = Path.Combine(temp, "mcp.log");
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        try
        {
            var scriptPath = Path.Combine(temp, "recover.ps1");
            File.WriteAllText(scriptPath, result.GeneratedPowerShell);
            File.WriteAllText(Path.Combine(temp, "Writ.psm1"), PowerShellGenerator.RuntimeModuleSource);
            var (code, stdout, stderr) = RunPwsh(scriptPath, "-ListTools");
            Assert.True(code == 0, stderr);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("payments.list_failed (stdio) ", lines[0]);
            Assert.StartsWith("payments.retry (stdio) ", lines[1]);
            Assert.Contains("mcp-test-server.ps1", stdout);
            Assert.Contains("__Assert-WritMcpTools", result.GeneratedPowerShell);
            Assert.False(File.Exists(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            restore.Dispose();
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void ListTools_on_a_script_with_no_tools_prints_nothing()
    {
        var result = new CompilationPipeline().Compile("""
            When run
                say "hi"
            """, "hi.writ");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var temp = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var scriptPath = Path.Combine(temp, "hi.ps1");
            File.WriteAllText(scriptPath, result.GeneratedPowerShell);
            File.WriteAllText(Path.Combine(temp, "Writ.psm1"), PowerShellGenerator.RuntimeModuleSource);
            var (code, stdout, stderr) = RunPwsh(scriptPath, "-ListTools");
            Assert.True(code == 0, stderr);
            Assert.Equal("", stdout.Trim());
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Starting_a_script_checks_required_tools_then_runs()
    {
        var (source, log) = TallyScript();
        var result = new CompilationPipeline().Compile(source, Path.Combine(Path.GetTempPath(), "tally.writ"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Assert-WritMcpTools -RequiredJson", result.GeneratedPowerShell);

        var temp = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var scriptPath = Path.Combine(temp, "tally.ps1");
            File.WriteAllText(scriptPath, result.GeneratedPowerShell);
            File.WriteAllText(Path.Combine(temp, "Writ.psm1"), PowerShellGenerator.RuntimeModuleSource);
            var (code, stdout, stderr) = RunScript(scriptPath, new Dictionary<string, string?> { ["INTENT_MCP_LOG"] = log }, "-day", "2026-10-20");
            Assert.True(code == 0, stderr + stdout);
            Assert.Equal("2", stdout.Trim());
            var methods = File.ReadAllLines(log);
            var listed = Array.IndexOf(methods, "tools/list");
            var called = Array.IndexOf(methods, "tools/call");
            Assert.True(listed >= 0 && called > listed);
        }
        finally
        {
            Directory.Delete(temp, true);
            if (File.Exists(log))
            {
                File.Delete(log);
            }
        }
    }

    [Fact]
    public void Starting_a_script_stops_when_a_required_tool_is_missing()
    {
        var (source, log) = TallyScript();
        var result = new CompilationPipeline().Compile(source, Path.Combine(Path.GetTempPath(), "tally.writ"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var temp = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("INTENT_MCP_OMIT", "list_failed");
        Environment.SetEnvironmentVariable("INTENT_MCP_LOG", log);
        try
        {
            var scriptPath = Path.Combine(temp, "tally.ps1");
            File.WriteAllText(scriptPath, result.GeneratedPowerShell);
            File.WriteAllText(Path.Combine(temp, "Writ.psm1"), PowerShellGenerator.RuntimeModuleSource);
            var (code, stdout, stderr) = RunScript(scriptPath, new Dictionary<string, string?>
            {
                ["INTENT_MCP_LOG"] = log,
                ["INTENT_MCP_OMIT"] = "list_failed"
            }, "-day", "2026-10-20");
            Assert.NotEqual(0, code);
            Assert.Contains("payments.list_failed", stderr + stdout);
            Assert.Contains("is not available", stderr + stdout);
            var methods = File.ReadAllLines(log);
            Assert.Contains("tools/list", methods);
            Assert.DoesNotContain("tools/call", methods);

            var ran = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "tally", "Main", """{ "day": "2026-10-20" }""");
            Assert.False(ran.Success);
            Assert.Contains("payments.list_failed", string.Join(Environment.NewLine, ran.Errors));
            Assert.Contains("is not available", string.Join(Environment.NewLine, ran.Errors));
            Assert.Equal(0, ran.CapabilityCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_MCP_OMIT", null);
            Environment.SetEnvironmentVariable("INTENT_MCP_LOG", null);
            Directory.Delete(temp, true);
            if (File.Exists(log))
            {
                File.Delete(log);
            }
        }
    }

    [Fact]
    public void Rest_tool_is_discovered_and_runs_on_both_targets()
    {
        using var server = McpHttp.Start();
        using var restore = McpDefaults.OverrideUserFile("");
        var source = $$"""
            Use search from mcp "{{server.Url}}"
            Allow network access

            To Ping, returning a string
                the response is ask search to echo with text set to "hi"
                return the response as a string
            """;
        var result = new CompilationPipeline().Compile(source, Path.Combine(Path.GetTempPath(), "echo.writ"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("rest", Assert.Single(result.Capabilities).Transport);
        Assert.Contains("tools/list", server.Methods);
        Assert.DoesNotContain("tools/call", server.Methods);
        Assert.Contains("-Transport 'rest'", result.GeneratedPowerShell);

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "echo", "Ping", "{}");
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors) + pwsh.Output);
        Assert.Equal("hi", pwsh.Value);
        Assert.Contains("tools/call", server.Methods);
    }

    private const string Tally = """
        Allow network access

        To Tally, given a day which is a string, returning an integer
            the failures are ask payments to list_failed with the day
            return the count of the failures
        """;

    private static string Touch(string path) =>
        "pwsh -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath '" + path.Replace('\\', '/') + "' -Value started\"";

    private static string Servers(params (string Alias, string Command)[] servers)
    {
        var payload = new Dictionary<string, object>
        {
            ["servers"] = servers.Select(server => new Dictionary<string, string>
            {
                ["alias"] = server.Alias,
                ["command"] = server.Command
            }).ToArray()
        };
        return JsonSerializer.Serialize(payload);
    }

    private static (string Source, string Log) TallyScript()
    {
        var server = Path.Combine(TestPaths.RepoRoot(), "tools", "mcp-test-server.ps1").Replace('\\', '/');
        var command = $"pwsh -NoProfile -NonInteractive -File {server}";
        var log = Path.Combine(Path.GetTempPath(), "intent-mcp-" + Guid.NewGuid().ToString("N") + ".log");
        var source = $$"""
            Use payments from mcp "{{command}}"
            Allow network access

            When run, given a day which is a string, returning an integer
                the failures are ask payments to list_failed with the day
                return the count of the failures
            """;
        return (source, log);
    }

    private static (int Code, string Stdout, string Stderr) RunPwsh(string scriptPath, string argument) =>
        RunScript(scriptPath, null, argument);

    private static (int Code, string Stdout, string Stderr) RunScript(string scriptPath, IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private sealed class McpHttp : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _thread;
        private readonly object _gate = new();
        private readonly List<string> _methods = [];

        public string Url { get; }

        public IReadOnlyList<string> Methods
        {
            get
            {
                lock (_gate)
                {
                    return _methods.ToList();
                }
            }
        }

        private McpHttp(HttpListener listener, int port)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{port}/mcp";
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public static McpHttp Start()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var tcp = new TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return new McpHttp(listener, port);
                }
                catch
                {
                    listener.Close();
                }
            }

            throw new InvalidOperationException("Could not listen on localhost.");
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch
                {
                    break;
                }

                try
                {
                    Handle(context);
                }
                catch
                {
                    try
                    {
                        context.Response.Abort();
                    }
                    catch
                    {
                        // The client has gone.
                    }
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var encoding = context.Request.ContentEncoding ?? Encoding.UTF8;
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, encoding))
            {
                body = reader.ReadToEnd();
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() ?? "" : "";
            lock (_gate)
            {
                _methods.Add(method);
            }

            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetRawText() : "null";
            context.Response.Headers["Mcp-Session-Id"] = "test-session";
            if (method == "notifications/initialized")
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            var sse = method == "tools/list";
            var payload = method switch
            {
                "tools/list" => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]},\"annotations\":{\"readOnlyHint\":true}}]}}",
                "tools/call" => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"isError\":false}}",
                _ => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"echo\",\"version\":\"0\"}}}"
            };
            var text = sse ? "event: message\ndata: " + payload + "\n\n" : payload;
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = 200;
            context.Response.ContentType = sse ? "text/event-stream" : "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
    }
}

[CollectionDefinition("mcp", DisableParallelization = true)]
public sealed class McpCollection
{
}
