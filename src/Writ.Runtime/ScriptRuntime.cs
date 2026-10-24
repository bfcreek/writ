using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Writ.Runtime;

/// <summary>
/// Runs a Writ script or a PowerShell script. The compiler never calls this type.
/// Writ scripts are already compiled to PowerShell; this starts pwsh and reads the result back.
/// </summary>
public static class ScriptRuntime
{
    private const string Marker = "__INTENT_RESULT__";

    private static readonly Lazy<string> ModuleSource = new(ReadModule);

    private const string Runner = """
        param([string]$ScriptPath, [string]$InputPath)
        $ErrorActionPreference = 'Stop'
        . $ScriptPath
        $__raw = Get-Content -LiteralPath $InputPath -Raw
        $__input = if ([string]::IsNullOrWhiteSpace($__raw)) { [pscustomobject]@{} } else { $__raw | ConvertFrom-Json }
        $__params = @{}
        foreach ($__p in @($__input.PSObject.Properties)) {
            $__params[$__p.Name] = $__p.Value
        }
        $__result = & Main @__params
        Write-Output '__INTENT_RESULT__'
        if ($null -eq $__result) {
            Write-Output 'null'
        } else {
            Write-Output (ConvertTo-Json -InputObject $__result -Depth 32 -Compress)
        }
        """;

    public static T Run<T>(string path, string kind, string? compiledScript, bool writes, IReadOnlyDictionary<string, object>? arguments)
    {
        arguments ??= new Dictionary<string, object>();
        if (writes && Environment.GetEnvironmentVariable("INTENT_DRY_RUN") == "1")
        {
            Console.WriteLine($"What if: would run {path}");
            return default!;
        }

        Log(path);
        if (string.Equals(kind, "writ", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(compiledScript))
            {
                throw new InvalidOperationException($"No compiled script was provided for '{path}'.");
            }

            var json = RunWrit(compiledScript, arguments);
            if (json == "null")
            {
                return default!;
            }

            if (typeof(T) == typeof(object))
            {
                using var document = JsonDocument.Parse(json);
                return (T)(ToClr(document.RootElement) ?? new object());
            }

            var value = JsonSerializer.Deserialize<T>(json);
            if (value is null)
            {
                throw new InvalidOperationException($"Script '{path}' returned nothing.");
            }

            return value;
        }

        var text = RunPowerShellFile(path, arguments).Trim();
        if (typeof(T) == typeof(string))
        {
            return (T)(object)text;
        }

        if (typeof(T) == typeof(object))
        {
            return (T)(object)text;
        }

        var parsed = JsonSerializer.Deserialize<T>(text);
        if (parsed is null)
        {
            throw new InvalidOperationException($"Script '{path}' returned nothing.");
        }

        return parsed;
    }

    private static string RunWrit(string compiledScript, IReadOnlyDictionary<string, object> arguments)
    {
        var dir = Path.Combine(Path.GetTempPath(), "writ-call-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Writ.psm1"), ModuleSource.Value, new UTF8Encoding(true));
            var scriptPath = Path.Combine(dir, "callee.ps1");
            File.WriteAllText(scriptPath, compiledScript, new UTF8Encoding(true));
            var inputPath = Path.Combine(dir, "input.json");
            File.WriteAllText(inputPath, JsonSerializer.Serialize(arguments));
            var runnerPath = Path.Combine(dir, "runner.ps1");
            File.WriteAllText(runnerPath, Runner, new UTF8Encoding(true));

            var start = StartInfo(dir);
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(runnerPath);
            start.ArgumentList.Add("-ScriptPath");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-InputPath");
            start.ArgumentList.Add(inputPath);
            var (exit, stdout, stderr) = Run(start);
            if (exit != 0)
            {
                throw new InvalidOperationException(Failure("writ script", exit, stdout, stderr));
            }

            var index = stdout.LastIndexOf(Marker, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException($"Script did not return a result. {stdout}");
            }

            return stdout[(index + Marker.Length)..].Trim();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string RunPowerShellFile(string path, IReadOnlyDictionary<string, object> arguments)
    {
        var start = StartInfo(Path.GetDirectoryName(path));
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(path);
        foreach (var (name, value) in arguments)
        {
            start.ArgumentList.Add("-" + name);
            if (value is null)
            {
                continue;
            }

            start.ArgumentList.Add(value switch
            {
                bool b => b ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            });
        }

        var (exit, stdout, stderr) = Run(start);
        if (exit != 0)
        {
            throw new InvalidOperationException(Failure(path, exit, stdout, stderr));
        }

        return stdout;
    }

    private static ProcessStartInfo StartInfo(string? workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            start.WorkingDirectory = workingDirectory;
        }

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        return start;
    }

    private static (int Exit, string Stdout, string Stderr) Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pwsh.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // The process is being abandoned.
            }

            throw new InvalidOperationException("Script did not finish within 120 seconds.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string Failure(string path, int exit, string stdout, string stderr)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        return $"Script '{path}' failed ({exit}). {detail}".Trim();
    }

    private static void Log(string path)
    {
        var log = Environment.GetEnvironmentVariable("INTENT_CALL_LOG");
        if (!string.IsNullOrEmpty(log))
        {
            File.AppendAllText(log, "script:" + Path.GetFileName(path) + Environment.NewLine);
        }
    }

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) && !element.GetRawText().Contains('.') => l,
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => element.GetRawText()
    };

    private static string ReadModule()
    {
        using var stream = typeof(ScriptRuntime).Assembly.GetManifestResourceStream("Writ.psm1")
            ?? throw new InvalidOperationException("Writ.psm1 is not embedded in the runtime.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
