using System.Text.Json;

namespace Writ.Compiler.Capabilities;

public sealed class McpDefaultServer
{
    public string? Alias { get; set; }
    public string? Command { get; set; }
    public string? Url { get; set; }
}

/// <summary>One server after closer files have overridden farther ones.</summary>
public sealed record McpServerDecl(string Alias, string Endpoint, string Transport, string? WorkingDirectory, string DefinedIn);

public sealed record McpConfigError(string Code, string Message);

public sealed class McpConfig
{
    public required IReadOnlyList<McpServerDecl> Servers { get; init; }
    public required IReadOnlyList<McpConfigError> Errors { get; init; }
}

/// <summary>
/// MCP servers declared once for every program under a folder.
/// Files named <c>writ.mcp.json</c> are read from the source directory upward,
/// then from <c>%USERPROFILE%\.writ\writ.mcp.json</c>. The closest definition of an alias wins.
/// </summary>
public static class McpDefaults
{
    public const string FileName = "writ.mcp.json";

    private static readonly AsyncLocal<string?> UserOverride = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Tests point this at a stand-in user file. An empty path means there is no user file.
    /// Dispose restores the previous value. The override follows the calling flow, not the process.
    /// </summary>
    public static IDisposable OverrideUserFile(string? path)
    {
        var previous = UserOverride.Value;
        UserOverride.Value = path ?? "";
        return new Restore(previous);
    }

    /// <summary>The user-level file, when it exists. An override replaces the real home file.</summary>
    public static string? UserFile()
    {
        if (UserOverride.Value is { } overridden)
        {
            return overridden.Length == 0 || !File.Exists(overridden) ? null : Path.GetFullPath(overridden);
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".writ", FileName);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>Walks up from the source file. Returns null when no project defaults file is found.</summary>
    public static string? Find(string? sourceFile)
    {
        foreach (var path in Files(sourceFile))
        {
            var user = UserFile();
            if (user is null || !string.Equals(path, user, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    public static IReadOnlyList<McpDefaultServer> Read(string path)
    {
        var file = JsonSerializer.Deserialize<McpDefaultsFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("The file is empty.");
        return file.Servers ?? [];
    }

    /// <summary>
    /// Closest project file first, user file last. The first definition of an alias is kept.
    /// A repeated alias in the same file is an error. A later file's alias is the fallback.
    /// </summary>
    public static McpConfig Merge(string? sourceFile)
    {
        var servers = new Dictionary<string, McpServerDecl>(StringComparer.Ordinal);
        var errors = new List<McpConfigError>();
        foreach (var path in Files(sourceFile))
        {
            ReadInto(path, servers, errors);
        }

        return new McpConfig { Servers = servers.Values.ToList(), Errors = errors };
    }

    private static IReadOnlyList<string> Files(string? sourceFile)
    {
        var files = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceFile))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, FileName);
                if (File.Exists(candidate))
                {
                    files.Add(Path.GetFullPath(candidate));
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        var user = UserFile();
        if (user is not null && !files.Contains(user, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(user);
        }

        return files;
    }

    private static void ReadInto(string path, Dictionary<string, McpServerDecl> servers, List<McpConfigError> errors)
    {
        McpDefaultsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<McpDefaultsFile>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            errors.Add(new McpConfigError("E4001", $"{path} is not valid: {ex.Message}"));
            return;
        }

        var directory = Path.GetDirectoryName(path);
        foreach (var server in file?.Servers ?? [])
        {
            var alias = server.Alias?.Trim() ?? "";
            var command = server.Command?.Trim() ?? "";
            var url = server.Url?.Trim() ?? "";
            if (alias.Length == 0)
            {
                errors.Add(new McpConfigError("E4001", $"{path} has a server without an alias."));
                continue;
            }

            if (command.Length > 0 && url.Length > 0)
            {
                errors.Add(new McpConfigError("E4001", $"{path} declares '{alias}' with both a command and a url."));
                continue;
            }

            if (command.Length == 0 && url.Length == 0)
            {
                errors.Add(new McpConfigError("E4001", $"{path} declares '{alias}' without a command or a url."));
                continue;
            }

            if (url.Length > 0 && !McpDiscovery.IsRest(url))
            {
                errors.Add(new McpConfigError("E4001", $"{path} declares '{alias}' with a url that is not http:// or https://."));
                continue;
            }

            if (servers.TryGetValue(alias, out var existing))
            {
                if (string.Equals(existing.DefinedIn, path, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new McpConfigError("E4004", $"{path} declares '{alias}' more than once."));
                }

                continue;
            }

            var endpoint = url.Length > 0 ? url : command;
            servers[alias] = new McpServerDecl(alias, endpoint, McpDiscovery.TransportOf(endpoint), directory, path);
        }
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => UserOverride.Value = previous;
    }

    private sealed class McpDefaultsFile
    {
        public List<McpDefaultServer>? Servers { get; set; }
    }
}
