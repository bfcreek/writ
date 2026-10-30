using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace Writ.Runtime;

public static partial class Machine
{
    public static object? InvokeCommand(string name, Dictionary<string, object?> arguments)
    {
        arguments = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase);
        Ambient.Value?.CallLog.Add(name);
        if (TryMockTool(name, out var mocked))
        {
            return mocked;
        }

        if (DryRun && ToolRegistry.IsWrite(name))
        {
            var safe = name.StartsWith("secret.", StringComparison.OrdinalIgnoreCase)
                ? "{}"
                : JsonSerializer.Serialize(arguments);
            Say($"What if: would run {name} with {safe}");
            return null;
        }

        if (ToolRegistry.TryInvoke(name, arguments, out var result))
        {
            return result;
        }

        throw new NotSupportedException($"Command '{name}' is not implemented by the C# Machine runtime.");
    }

    /// <summary>Registers catalog-backed handlers. Called once from <see cref="ToolRegistry"/>.</summary>
    internal static void RegisterBuiltinTools()
    {
        void R(string name, Func<Dictionary<string, object?>, object?> handler, bool writes = false) =>
            ToolRegistry.Register(name, handler, writes);

        R("Get-ChildItem", GetChildItem);
        R("Get-Item", GetItem);
        R("Get-Content", args => File.ReadAllText(RequiredPath(args)));
        R("Set-Content", args => WriteFile(args, append: false), writes: true);
        R("Add-Content", args => WriteFile(args, append: true), writes: true);
        R("New-Item", NewItem, writes: true);
        R("Copy-Item", CopyItem, writes: true);
        R("Move-Item", MoveItem, writes: true);
        R("Rename-Item", RenameItem, writes: true);
        R("Remove-Item", RemoveItem, writes: true);
        R("Test-Path", args => File.Exists(RequiredPath(args)) || Directory.Exists(RequiredPath(args)));
        R("Join-Path", args => Path.Combine(Text(Arg(args, "Path")), Text(Arg(args, "ChildPath"))));
        R("Split-Path", SplitPath);
        R("Resolve-Path", args => Path.GetFullPath(RequiredPath(args)));
        R("Get-FileHash", GetFileHash);
        R("Select-String", SelectString);
        R("Compress-Archive", CompressArchive, writes: true);
        R("Expand-Archive", ExpandArchive, writes: true);
        R("New-TemporaryFile", _ => NewTemporaryFile(), writes: true);
        R("Set-Location", SetLocation, writes: true);
        R("Get-Date", GetDate);
        R("Get-Random", args => GetRandom(args));
        R("New-Guid", _ => Guid.NewGuid().ToString());
        R("ConvertTo-Json", args => JsonSerializer.Serialize(Arg(args, "InputObject"), new JsonSerializerOptions { WriteIndented = false }));
        R("Measure-Object", MeasureObject);
        R("Start-Sleep", Sleep, writes: true);
        R("Clear-Host", _ => ClearHost(), writes: true);
        R("Read-Host", args => Read(Text(Arg(args, "Prompt"))));
        R("Import-Csv", args => ParseCsv(File.ReadAllText(RequiredPath(args))));
        R("Export-Csv", ExportCsv, writes: true);
        R("Import-Module", _ => null);
        R("Remove-Module", _ => null);
        R("throw", args => throw new MachineHaltException(Text(args.Values.FirstOrDefault())));
        R("Write-Warning", args => WriteMessage("WARNING: " + Text(Arg(args, "Message"))));
        R("Write-Verbose", args => WriteMessage("VERBOSE: " + Text(Arg(args, "Message"))));

        RegisterOsTools();
        RegisterNativeTools();
    }

    private static object? WriteMessage(string message)
    {
        Say(message);
        return null;
    }

    private static List<WritData> GetChildItem(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        var result = new List<WritData>();
        if (!Directory.Exists(path))
        {
            return result;
        }

        var includeFiles = !arguments.ContainsKey("Directory");
        var includeDirectories = !arguments.ContainsKey("File");
        if (includeFiles)
        {
            result.AddRange(new DirectoryInfo(path).GetFiles().Select(FileData));
        }

        if (includeDirectories)
        {
            result.AddRange(new DirectoryInfo(path).GetDirectories().Select(DirectoryData));
        }

        return result;
    }

    private static WritData GetItem(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        if (File.Exists(path))
        {
            return FileData(new FileInfo(path));
        }

        if (Directory.Exists(path))
        {
            return DirectoryData(new DirectoryInfo(path));
        }

        throw new FileNotFoundException("Item was not found.", path);
    }

    private static object? WriteFile(Dictionary<string, object?> arguments, bool append)
    {
        var path = RequiredPath(arguments);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var text = Text(Arg(arguments, "Value") ?? Arg(arguments, "InputObject"));
        if (append)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }
        else
        {
            File.WriteAllText(path, text);
        }

        return null;
    }

    private static WritData NewItem(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        var itemType = Text(Arg(arguments, "ItemType"));
        if (itemType.Equals("Directory", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(path);
            return DirectoryData(new DirectoryInfo(path));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using (File.Create(path))
        {
        }

        return FileData(new FileInfo(path));
    }

    private static object? CopyItem(Dictionary<string, object?> arguments)
    {
        var source = RequiredPath(arguments);
        var destination = Text(Arg(arguments, "Destination"));
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
        else
        {
            File.Copy(source, destination, overwrite: true);
        }

        return null;
    }

    private static object? MoveItem(Dictionary<string, object?> arguments)
    {
        var source = RequiredPath(arguments);
        var destination = Text(Arg(arguments, "Destination"));
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: true);
        }

        return null;
    }

    private static object? RenameItem(Dictionary<string, object?> arguments)
    {
        var source = RequiredPath(arguments);
        var newName = Text(Arg(arguments, "NewName"));
        var destination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".", newName);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: true);
        }

        return null;
    }

    private static object? RemoveItem(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: ToBoolean(Arg(arguments, "Recurse")));
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        return null;
    }

    private static object? SplitPath(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        if (ToBoolean(Arg(arguments, "Leaf")))
        {
            return Path.GetFileName(path);
        }

        if (ToBoolean(Arg(arguments, "LeafBase")))
        {
            return Path.GetFileNameWithoutExtension(path);
        }

        if (ToBoolean(Arg(arguments, "Extension")))
        {
            return Path.GetExtension(path);
        }

        return Path.GetDirectoryName(path);
    }

    private static WritData GetFileHash(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return new WritData(new Dictionary<string, object?>
        {
            ["Algorithm"] = "SHA256",
            ["Hash"] = Convert.ToHexString(hash),
            ["Path"] = path
        });
    }

    private static List<WritData> SelectString(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        var pattern = Text(Arg(arguments, "Pattern"));
        var options = ToBoolean(Arg(arguments, "CaseSensitive")) ? RegexOptions.None : RegexOptions.IgnoreCase;
        var regex = new Regex(pattern, options);
        var result = new List<WritData>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (regex.IsMatch(line))
            {
                result.Add(new WritData(new Dictionary<string, object?>
                {
                    ["Path"] = path,
                    ["Line"] = line,
                    ["LineNumber"] = (long)number
                }));
            }
        }

        return result;
    }

    private static object? CompressArchive(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        var destination = Text(Arg(arguments, "DestinationPath"));
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        if (Directory.Exists(path))
        {
            ZipFile.CreateFromDirectory(path, destination);
        }
        else
        {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(path, Path.GetFileName(path));
        }

        return null;
    }

    private static object? ExpandArchive(Dictionary<string, object?> arguments)
    {
        ZipFile.ExtractToDirectory(RequiredPath(arguments), Text(Arg(arguments, "DestinationPath")), overwriteFiles: true);
        return null;
    }

    private static WritData NewTemporaryFile()
    {
        var path = Path.GetTempFileName();
        return FileData(new FileInfo(path));
    }

    private static object? SetLocation(Dictionary<string, object?> arguments)
    {
        Directory.SetCurrentDirectory(RequiredPath(arguments));
        return null;
    }

    private static object GetDate(Dictionary<string, object?> arguments)
    {
        var now = DateTime.Now;
        var format = Text(Arg(arguments, "Format"));
        return string.IsNullOrEmpty(format) ? now : now.ToString(format);
    }

    private static long GetRandom(Dictionary<string, object?> arguments)
    {
        var min = arguments.TryGetValue("Minimum", out var minimum) ? ToInteger(minimum) : 0;
        var max = arguments.TryGetValue("Maximum", out var maximum) ? ToInteger(maximum) : int.MaxValue;
        return Random.Shared.NextInt64(min, max);
    }

    private static WritData MeasureObject(Dictionary<string, object?> arguments)
    {
        var input = Text(Arg(arguments, "InputObject"));
        return new WritData(new Dictionary<string, object?>
        {
            ["Words"] = ToBoolean(Arg(arguments, "Word")) ? Regex.Matches(input, @"\S+").Count : 0,
            ["Lines"] = ToBoolean(Arg(arguments, "Line")) ? input.Split('\n').LongLength : 0,
            ["Count"] = 1L
        });
    }

    private static object? Sleep(Dictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("Milliseconds", out var ms))
        {
            Wait(ToInteger(ms), milliseconds: true);
        }
        else
        {
            Wait(ToInteger(Arg(arguments, "Seconds")), milliseconds: false);
        }

        return null;
    }

    private static object? ClearHost()
    {
        Clear();
        return null;
    }

    private static object? ExportCsv(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        var rows = AsEnumerable(Arg(arguments, "InputObject")).OfType<object>().ToList();
        var fields = rows.SelectMany(row => FieldNames(row)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", fields.Select(CsvEscape)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", fields.Select(field => CsvEscape(Text(Field(row, field, optional: true))))));
        }

        File.WriteAllText(path, sb.ToString());
        return null;
    }

    private static object? Arg(Dictionary<string, object?> arguments, string name)
    {
        if (arguments.TryGetValue(name, out var value))
        {
            return value;
        }

        return null;
    }

    private static string RequiredPath(Dictionary<string, object?> arguments) =>
        Text(Arg(arguments, "LiteralPath") ?? Arg(arguments, "Path"));

    private static WritData FileData(FileInfo file) => new(new Dictionary<string, object?>
    {
        ["Name"] = file.Name,
        ["FullName"] = file.FullName,
        ["Length"] = file.Exists ? file.Length : 0L,
        ["DirectoryName"] = file.DirectoryName ?? "",
        ["Extension"] = file.Extension,
        ["BaseName"] = Path.GetFileNameWithoutExtension(file.Name),
        ["Exists"] = file.Exists,
        ["LastWriteTime"] = file.Exists ? file.LastWriteTime.ToString("o") : "",
        ["CreationTime"] = file.Exists ? file.CreationTime.ToString("o") : "",
        ["LastAccessTime"] = file.Exists ? file.LastAccessTime.ToString("o") : ""
    });

    private static WritData DirectoryData(DirectoryInfo directory) => new(new Dictionary<string, object?>
    {
        ["Name"] = directory.Name,
        ["FullName"] = directory.FullName,
        ["Length"] = 0L,
        ["Parent"] = directory.Parent?.FullName ?? "",
        ["Exists"] = directory.Exists,
        ["LastWriteTime"] = directory.Exists ? directory.LastWriteTime.ToString("o") : "",
        ["CreationTime"] = directory.Exists ? directory.CreationTime.ToString("o") : "",
        ["LastAccessTime"] = directory.Exists ? directory.LastAccessTime.ToString("o") : ""
    });

    private static IEnumerable AsEnumerable(object? value) =>
        value is IEnumerable enumerable and not string ? enumerable : new[] { value };

    private static IEnumerable<string> FieldNames(object row) => row switch
    {
        WritData data => data.Keys,
        IReadOnlyDictionary<string, object?> dictionary => dictionary.Keys,
        _ => row.GetType().GetProperties().Select(property => property.Name)
    };

    private static string CsvEscape(string value) =>
        value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
