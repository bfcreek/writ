using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Writ.Compiler.Codegen;

namespace Writ.Compiler.Execution;

/// <summary>
/// Runs a generated script with pwsh. Capability calls happen only here, never in the compiler.
/// </summary>
public sealed class PowerShellExecutor
{
    private const string Marker = "__INTENT_RESULT__";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public bool DryRun { get; init; }

    public ExecutionResult Execute(string generatedScript, string moduleName, string entryFunction, string? inputJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            PowerShellGenerator.InstallRuntimeModule();
            var scriptPath = Path.Combine(folder, Sanitize(moduleName) + ".ps1");
            File.WriteAllText(scriptPath, generatedScript, new UTF8Encoding(true));
            var inputPath = Path.Combine(folder, "input.json");
            File.WriteAllText(inputPath, string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            var callLog = Path.Combine(folder, "calls.log");
            var runnerPath = Path.Combine(folder, "runner.ps1");
            File.WriteAllText(runnerPath, Runner, new UTF8Encoding(true));

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(runnerPath);
            startInfo.ArgumentList.Add("-Script");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Entry");
            startInfo.ArgumentList.Add(Sanitize(entryFunction));
            startInfo.ArgumentList.Add("-InputPath");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.Environment["INTENT_CALL_LOG"] = callLog;
            if (DryRun)
            {
                startInfo.Environment["INTENT_DRY_RUN"] = "1";
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start pwsh.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                process.Kill(true);
                return ExecutionResult.Failed([$"pwsh did not finish within {Timeout.TotalSeconds:0} seconds."]);
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var calls = File.Exists(callLog) ? File.ReadAllLines(callLog).Count(line => line.Length > 0) : 0;

            var markerIndex = stdout.LastIndexOf(Marker, StringComparison.Ordinal);
            if (process.ExitCode != 0 || markerIndex < 0)
            {
                var errors = StructuredErrors(stdout, stderr);
                if (errors.Count == 0)
                {
                    errors.Add($"pwsh exited with code {process.ExitCode}.");
                }

                return ExecutionResult.Failed(errors) with { CapabilityCalls = calls };
            }

            var json = stdout[(markerIndex + Marker.Length)..].Trim();
            var printed = stdout[..markerIndex];
            object? value;
            try
            {
                using var document = JsonDocument.Parse(json);
                value = ToClr(document.RootElement);
            }
            catch (JsonException ex)
            {
                return ExecutionResult.Failed([$"Could not read the script result: {ex.Message}", json]);
            }

            return ExecutionResult.Ok(value) with { CapabilityCalls = calls, Output = printed };
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    public static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) && !element.GetRawText().Contains('.') ? l : element.GetDecimal(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => element.GetRawText()
    };

    private const string Runner = """
        param([string]$Script, [string]$Entry, [string]$InputPath)
        $ErrorActionPreference = 'Stop'
        . $Script
        $__input = [pscustomobject]@{}
        if ($InputPath -and (Test-Path -LiteralPath $InputPath)) {
            $__raw = Get-Content -LiteralPath $InputPath -Raw
            if ($__raw.Trim()) { $__input = $__raw | ConvertFrom-Json }
        }
        try {
            $__result = & "__Invoke_$Entry" $__input
        }
        catch {
            $__ex = $_.Exception
            $__info = [ordered]@{ message = [string]$__ex.Message; kind = $__ex.GetType().Name }
            if ($__ex.PSObject.Properties['Alias']) { $__info.alias = $__ex.Alias; $__info.operation = $__ex.Operation; $__info.provider = $__ex.Provider }
            $__line = $_.InvocationInfo.ScriptLineNumber
            if ($__line) { $__info.scriptLine = $__line }
            Write-Output '__INTENT_ERROR__'
            Write-Output (ConvertTo-Json -InputObject $__info -Compress)
            exit 1
        }
        Write-Output '__INTENT_RESULT__'
        Write-Output (ConvertTo-Json -InputObject $__result -Depth 32 -Compress)
        """;

    private const string ErrorMarker = "__INTENT_ERROR__";

    /// <summary>Turns the runner's structured error into one readable line plus the raw output.</summary>
    private static List<string> StructuredErrors(string stdout, string stderr)
    {
        var errors = new List<string>();
        var index = stdout.LastIndexOf(ErrorMarker, StringComparison.Ordinal);
        if (index >= 0)
        {
            try
            {
                using var document = JsonDocument.Parse(stdout[(index + ErrorMarker.Length)..].Trim());
                var root = document.RootElement;
                var message = root.GetProperty("message").GetString() ?? "The script failed.";
                if (root.TryGetProperty("alias", out var alias))
                {
                    message = $"{alias.GetString()}.{root.GetProperty("operation").GetString()} ({root.GetProperty("provider").GetString()}): {message}";
                }

                errors.Add(message);
                var printed = stdout[..index].Trim();
                if (printed.Length > 0)
                {
                    errors.Add(printed);
                }

                return errors;
            }
            catch (JsonException)
            {
                // Fall through to the raw text.
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            errors.Add(stderr.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            errors.Add(stdout.Trim());
        }

        return errors;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
        if (chars.Length == 0)
        {
            return "_";
        }

        return char.IsDigit(chars[0]) ? "_" + new string(chars) : new string(chars);
    }
}
