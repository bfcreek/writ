using System.Collections;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace Writ.Runtime;

public static partial class Machine
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(100) };

    internal static void RegisterOsTools()
    {
        void R(string name, Func<Dictionary<string, object?>, object?> handler, bool writes = false) =>
            ToolRegistry.Register(name, handler, writes);

        R("Get-Process", GetProcess);
        R("Stop-Process", StopProcess, writes: true);
        R("Wait-Process", WaitProcess);
        R("Start-Process", StartProcessTool, writes: true);
        R("Invoke-Item", InvokeItem, writes: true);
        R("Get-Command", GetCommandTool);
        R("Get-Service", GetService);
        R("Start-Service", args => ControlService(args, ServiceControllerStatus.Running), writes: true);
        R("Stop-Service", args => ControlService(args, ServiceControllerStatus.Stopped), writes: true);
        R("Restart-Service", RestartService, writes: true);
        R("Get-PSDrive", GetPsDrive);
        R("Get-TimeZone", _ => TimeZoneData(TimeZoneInfo.Local));
        R("Get-Uptime", _ => UptimeData());
        R("Stop-Computer", _ => PowerComputer("/s"), writes: true);
        R("Restart-Computer", _ => PowerComputer("/r"), writes: true);
        R("Invoke-RestMethod", InvokeRestMethod);
        R("Invoke-WebRequest", InvokeWebRequest);
        R("Test-Connection", args => TestConnection(args));
        R("Compare-Object", CompareObject);
        R("Get-Clipboard", _ => GetClipboard());
        R("Set-Clipboard", SetClipboard, writes: true);
        R("process.output", ProcessOutput, writes: true);
    }

    private static List<WritData> GetProcess(Dictionary<string, object?> arguments)
    {
        var name = Text(Arg(arguments, "Name"));
        var processes = string.IsNullOrWhiteSpace(name)
            ? Process.GetProcesses()
            : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
        return processes.Select(ProcessData).ToList();
    }

    private static object? StopProcess(Dictionary<string, object?> arguments)
    {
        var name = Text(Arg(arguments, "Name"));
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
        {
            process.Kill(entireProcessTree: true);
        }

        return null;
    }

    private static object? WaitProcess(Dictionary<string, object?> arguments)
    {
        var name = Text(Arg(arguments, "Name"));
        var timeout = arguments.ContainsKey("Timeout") ? (int)ToInteger(Arg(arguments, "Timeout")) : -1;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
        {
            if (timeout < 0)
            {
                process.WaitForExit();
            }
            else if (!process.WaitForExit(timeout * 1000))
            {
                throw new System.TimeoutException($"Process '{name}' did not exit within {timeout} seconds.");
            }
        }

        return null;
    }

    private static object? StartProcessTool(Dictionary<string, object?> arguments)
    {
        var file = Text(Arg(arguments, "FilePath"));
        var args = Text(Arg(arguments, "ArgumentList"));
        var wait = ToBoolean(Arg(arguments, "Wait"));
        var noNewWindow = ToBoolean(Arg(arguments, "NoNewWindow"));
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = !noNewWindow,
            CreateNoWindow = noNewWindow
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{file}'.");
        if (wait)
        {
            process.WaitForExit();
        }

        return null;
    }

    private static string ProcessOutput(Dictionary<string, object?> arguments)
    {
        var file = Text(Arg(arguments, "FilePath"));
        var args = Text(Arg(arguments, "ArgumentList"));
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{file}'.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"'{file}' exited {process.ExitCode}." : error.Trim());
        }

        return string.IsNullOrWhiteSpace(error) ? output : output + error;
    }

    private static object? InvokeItem(Dictionary<string, object?> arguments)
    {
        var path = RequiredPath(arguments);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return null;
    }

    private static WritData GetCommandTool(Dictionary<string, object?> arguments)
    {
        var name = Text(Arg(arguments, "Name"));
        var path = FindOnPath(name);
        if (path is null)
        {
            throw new FileNotFoundException($"Command '{name}' was not found on PATH.", name);
        }

        return new WritData(new Dictionary<string, object?>
        {
            ["Name"] = Path.GetFileName(path),
            ["Source"] = path,
            ["CommandType"] = "Application",
            ["Path"] = path
        });
    }

    private static string? FindOnPath(string name)
    {
        if (File.Exists(name))
        {
            return Path.GetFullPath(name);
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static List<WritData> GetService(Dictionary<string, object?> arguments)
    {
        EnsureWindowsServices();
        var name = Text(Arg(arguments, "Name"));
        IEnumerable<ServiceController> services = ServiceController.GetServices();
        if (!string.IsNullOrWhiteSpace(name))
        {
            services = services.Where(service =>
                service.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                service.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        return services.Select(ServiceData).ToList();
    }

    private static object? ControlService(Dictionary<string, object?> arguments, ServiceControllerStatus target)
    {
        EnsureWindowsServices();
        using var service = new ServiceController(Text(Arg(arguments, "Name")));
        if (target == ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.Running)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(1));
        }
        else if (target == ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(1));
        }

        return null;
    }

    private static object? RestartService(Dictionary<string, object?> arguments)
    {
        ControlService(arguments, ServiceControllerStatus.Stopped);
        ControlService(arguments, ServiceControllerStatus.Running);
        return null;
    }

    private static void EnsureWindowsServices()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows services are only available on Windows.");
        }
    }

    private static List<WritData> GetPsDrive(Dictionary<string, object?> arguments)
    {
        var name = Text(Arg(arguments, "Name"));
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady);
        if (!string.IsNullOrWhiteSpace(name))
        {
            drives = drives.Where(drive =>
                drive.Name.TrimEnd('\\', '/').Equals(name.TrimEnd('\\', '/', ':'), StringComparison.OrdinalIgnoreCase) ||
                drive.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }

        return drives.Select(drive => new WritData(new Dictionary<string, object?>
        {
            ["Name"] = drive.Name.TrimEnd('\\', '/').TrimEnd(':'),
            ["Root"] = drive.RootDirectory.FullName,
            ["Used"] = drive.TotalSize - drive.AvailableFreeSpace,
            ["Free"] = drive.AvailableFreeSpace,
            ["Provider"] = "FileSystem"
        })).ToList();
    }

    private static WritData TimeZoneData(TimeZoneInfo zone) => new(new Dictionary<string, object?>
    {
        ["Id"] = zone.Id,
        ["DisplayName"] = zone.DisplayName,
        ["BaseUtcOffset"] = zone.BaseUtcOffset.ToString(),
        ["SupportsDaylightSavingTime"] = zone.SupportsDaylightSavingTime
    });

    private static WritData UptimeData()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new WritData(new Dictionary<string, object?>
        {
            ["Uptime"] = uptime.ToString(),
            ["Days"] = (long)uptime.Days,
            ["Hours"] = (long)uptime.Hours,
            ["Minutes"] = (long)uptime.Minutes,
            ["TotalSeconds"] = (long)uptime.TotalSeconds
        });
    }

    private static object? PowerComputer(string flag)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Stop/Restart-Computer is only implemented on Windows.");
        }

        Process.Start(new ProcessStartInfo("shutdown", $"{flag} /t 0") { UseShellExecute = true });
        return null;
    }

    private static object? InvokeRestMethod(Dictionary<string, object?> arguments)
    {
        var uri = Text(Arg(arguments, "Uri"));
        using var response = SharedHttp.GetAsync(uri).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        try
        {
            return ParseJson(body);
        }
        catch
        {
            return body;
        }
    }

    private static WritData InvokeWebRequest(Dictionary<string, object?> arguments)
    {
        var uri = Text(Arg(arguments, "Uri"));
        using var response = SharedHttp.GetAsync(uri).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return new WritData(new Dictionary<string, object?>
        {
            ["StatusCode"] = (long)response.StatusCode,
            ["StatusDescription"] = response.ReasonPhrase ?? "",
            ["Content"] = body,
            ["Headers"] = new WritData(headers)
        });
    }

    private static bool TestConnection(Dictionary<string, object?> arguments)
    {
        var host = Text(Arg(arguments, "TargetName"));
        var count = arguments.ContainsKey("Count") ? (int)ToInteger(Arg(arguments, "Count")) : 1;
        using var ping = new Ping();
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            try
            {
                var reply = ping.Send(host, 3000);
                if (reply?.Status == IPStatus.Success)
                {
                    return true;
                }
            }
            catch (PingException)
            {
                // Quiet mode: false when unreachable.
            }
        }

        return false;
    }

    private static List<WritData> CompareObject(Dictionary<string, object?> arguments)
    {
        var left = AsEnumerable(Arg(arguments, "ReferenceObject")).Cast<object?>().Select(Text).ToHashSet(StringComparer.Ordinal);
        var right = AsEnumerable(Arg(arguments, "DifferenceObject")).Cast<object?>().Select(Text).ToHashSet(StringComparer.Ordinal);
        var result = new List<WritData>();
        foreach (var item in left.Except(right, StringComparer.Ordinal))
        {
            result.Add(new WritData(new Dictionary<string, object?> { ["InputObject"] = item, ["SideIndicator"] = "<=" }));
        }

        foreach (var item in right.Except(left, StringComparer.Ordinal))
        {
            result.Add(new WritData(new Dictionary<string, object?> { ["InputObject"] = item, ["SideIndicator"] = "=>" }));
        }

        return result;
    }

    private static string GetClipboard()
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command Get-Clipboard")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not read the clipboard.");
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return text.TrimEnd('\r', '\n');
        }

        foreach (var (file, args) in new[] { ("pbpaste", ""), ("xclip", "-selection clipboard -o"), ("xsel", "--clipboard --output") })
        {
            var path = FindOnPath(file);
            if (path is null)
            {
                continue;
            }

            var psi = new ProcessStartInfo(path, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process is null)
            {
                continue;
            }

            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return text;
        }

        throw new PlatformNotSupportedException("Clipboard read is not available on this system.");
    }

    private static object? SetClipboard(Dictionary<string, object?> arguments)
    {
        var text = Text(Arg(arguments, "Value"));
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd", "/c clip")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not write the clipboard.");
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit();
            return null;
        }

        foreach (var (file, args) in new[] { ("pbcopy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") })
        {
            var path = FindOnPath(file);
            if (path is null)
            {
                continue;
            }

            var psi = new ProcessStartInfo(path, args) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process is null)
            {
                continue;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit();
            return null;
        }

        throw new PlatformNotSupportedException("Clipboard write is not available on this system.");
    }

    private static WritData ProcessData(Process process)
    {
        string processName;
        long workingSet;
        try
        {
            processName = process.ProcessName;
            workingSet = process.WorkingSet64;
        }
        catch
        {
            processName = "";
            workingSet = 0;
        }

        return new WritData(new Dictionary<string, object?>
        {
            ["Id"] = (long)process.Id,
            ["ProcessName"] = processName,
            ["Name"] = processName,
            ["WorkingSet"] = workingSet
        });
    }

    private static WritData ServiceData(ServiceController service) => new(new Dictionary<string, object?>
    {
        ["Name"] = service.ServiceName,
        ["ServiceName"] = service.ServiceName,
        ["DisplayName"] = service.DisplayName,
        ["Status"] = service.Status.ToString()
    });
}
