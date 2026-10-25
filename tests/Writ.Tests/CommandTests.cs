using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class CommandTests
{
    [Fact]
    public void Folder_report_binds_cmdlets_and_runs()
    {
        var path = TestPaths.Sample(Path.Combine("files", "folder-report.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var body = result.Ir!.Functions[0].Body;
        var command = Assert.IsType<IrCommand>(Assert.IsType<IrAssign>(body[0]).Value);
        Assert.Equal("Get-ChildItem", command.Name);
        Assert.Contains(command.Arguments, a => a.Name == "Path" && a.Value is IrVar { Name: "folder" });
        Assert.Contains(command.Arguments, a => a.Name == "File");
        Assert.IsType<IrFilter>(Assert.IsType<IrAssign>(body[1]).Value);
        Assert.IsType<IrSort>(Assert.IsType<IrAssign>(body[2]).Value);
        Assert.IsType<IrTake>(Assert.IsType<IrAssign>(body[3]).Value);

        var script = result.GeneratedPowerShell!;
        Assert.Contains("Get-ChildItem -Path $folder -File", script);
        Assert.Contains("Sort-Object -Property 'length' -Descending", script);
        Assert.Contains("Select-Object -First 3L", script);

        var folder = Path.Combine(TestPaths.RepoRoot(), "samples", "files");
        var input = System.Text.Json.JsonSerializer.Serialize(new { folder });
        var exec = new PowerShellExecutor().Execute(script, "folder-report", "Report", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));

        var names = Assert.IsAssignableFrom<System.Collections.IList>(exec.Value).Cast<object>().Select(x => x.ToString()!).ToList();
        Assert.Equal(3, names.Count);
        var expected = new DirectoryInfo(folder).GetFiles().OrderByDescending(f => f.Length).Take(3).Select(f => f.Name).ToList();
        Assert.Equal(expected.OrderBy(x => x), names.OrderBy(x => x));
        Assert.Contains(names[0], exec.Output);
    }

    [Fact]
    public void Cmdlet_without_permission_fails_with_E4003()
    {
        var path = TestPaths.Sample(Path.Combine("errors", "no-permission.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E4003");
        Assert.Contains("Remove-Item", diagnostic.Message);
        Assert.Contains("Allow writing files", diagnostic.Message);
    }

    [Fact]
    public void Compile_never_runs_a_command()
    {
        var marker = Path.Combine(Path.GetTempPath(), "writ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(marker);
        try
        {
            var source = $$"""
                Allow writing files

                To Wipe, returning an integer
                    the target is "{{marker.Replace("\\", "\\\\")}}"
                    remove the target
                    return 0
                """;
            var result = new CompilationPipeline().Compile(source, "wipe.writ");
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("Remove-Item -Path $target", result.GeneratedPowerShell!);
            Assert.True(Directory.Exists(marker));
        }
        finally
        {
            Directory.Delete(marker, true);
        }
    }

    [Theory]
    [InlineData("basics/countdown.writ", "Countdown", "basics/input-countdown.json")]
    public void While_and_say_run(string sample, string entry, string inputFile)
    {
        var path = TestPaths.Sample(sample);
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var input = File.ReadAllText(TestPaths.Sample(inputFile));

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "countdown", entry, input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal(3L, Convert.ToInt64(pwsh.Value));
        Assert.Contains("tick 3", pwsh.Output);
    }

    [Theory]
    [InlineData("""
        clear the screen
        """, "Clear-Host")]
    [InlineData("""
        clear the console
        """, "Clear-Host")]
    [InlineData("""
        wait 2 seconds
        """, "Start-Sleep -Seconds 2L")]
    [InlineData("""
        the delay is 2
        pause for the delay
        """, "Start-Sleep -Seconds $delay")]
    [InlineData("""
        stop with "failed"
        """, "throw 'failed'")]
    [InlineData("""
        warn "missing"
        """, "Write-Warning -Message 'missing'")]
    [InlineData("""
        warn that the folder is missing
        """, "Write-Warning -Message 'that the folder is missing'")]
    [InlineData("""
        note "detail"
        """, "Write-Verbose -Message 'detail'")]
    [InlineData("""
        the name is read "Your name"
        """, "Read-Host -Prompt 'Your name'")]
    [InlineData("""
        the name is ask the user for "Your name"
        """, "Read-Host -Prompt 'Your name'")]
    [InlineData("""
        the question is "Your name"
        the answer is prompt for the question
        """, "Read-Host -Prompt $question")]
    [InlineData("""
        the folder is "C:/work"
        go to the folder
        """, "Set-Location -Path $folder")]
    [InlineData("""
        the folder is "C:/work"
        change to the folder
        """, "Set-Location -Path $folder")]
    [InlineData("""
        Allow writing files

        the line is "hello"
        the file is "a.txt"
        add the line to the file
        """, "Add-Content -Path $file -Value $line")]
    [InlineData("""
        Allow writing files

        the file is "a.txt"
        the destination is "b.txt"
        move the file to the destination
        """, "Move-Item -Path $file -Destination $destination")]
    [InlineData("""
        Allow writing files

        the file is "a.txt"
        the newname is "b.txt"
        rename the file to the newname
        """, "Rename-Item -Path $file -NewName $newname")]
    [InlineData("""
        the folder is "C:/work"
        the name is "a.txt"
        the file is the combined path of the folder and the name
        """, "Join-Path -Path $folder -ChildPath $name")]
    [InlineData("""
        the folder is "C:/work"
        the name is "a.txt"
        the combined path of the folder and the name
        """, "Join-Path -Path $folder -ChildPath $name")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the parent is the parent folder of the path
        """, "Split-Path -Path $path -Parent")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the file name of the path
        """, "Split-Path -Path $path -Leaf")]
    [InlineData("""
        Allow reading files

        the file is "C:/work/a.txt"
        the full is the full path of the file
        """, "Resolve-Path -Path $file")]
    [InlineData("""
        Allow changing processes

        the program is "notepad.exe"
        start the program
        """, "Start-Process -FilePath $program")]
    [InlineData("""
        Allow changing processes

        the service is "spooler"
        stop the service
        """, "Stop-Service -Name $service")]
    [InlineData("""
        Allow changing processes

        the service is "spooler"
        start the service
        """, "Start-Service -Name $service")]
    [InlineData("""
        Allow changing processes

        the service is "spooler"
        restart the service
        """, "Restart-Service -Name $service")]
    [InlineData("""
        Allow changing processes

        the process is "notepad"
        stop the process
        """, "Stop-Process -Name $process")]
    [InlineData("""
        wipe the console
        """, "Clear-Host")]
    [InlineData("""
        erase the window
        """, "Clear-Host")]
    [InlineData("""
        blank the display
        """, "Clear-Host")]
    [InlineData("""
        cls
        """, "Clear-Host")]
    [InlineData("""
        sleep for 3 seconds
        """, "Start-Sleep -Seconds 3L")]
    [InlineData("""
        hold 2 minutes
        """, "Start-Sleep -Seconds (2L * 60L)")]
    [InlineData("""
        wait 1 hour
        """, "Start-Sleep -Seconds (1L * 3600L)")]
    [InlineData("""
        delay 10 ms
        """, "Start-Sleep -Milliseconds 10L")]
    [InlineData("""
        the delay is 2
        wait for the delay
        """, "Start-Sleep -Seconds $delay")]
    [InlineData("""
        halt with "failed"
        """, "throw 'failed'")]
    [InlineData("""
        abort with "nope"
        """, "throw 'nope'")]
    [InlineData("""
        caution "missing"
        """, "Write-Warning -Message 'missing'")]
    [InlineData("""
        alert that the disk is full
        """, "Write-Warning -Message 'that the disk is full'")]
    [InlineData("""
        mention "detail"
        """, "Write-Verbose -Message 'detail'")]
    [InlineData("""
        the name is ask for "Your name"
        """, "Read-Host -Prompt 'Your name'")]
    [InlineData("""
        the name is ask the reader for "Your name"
        """, "Read-Host -Prompt 'Your name'")]
    [InlineData("""
        prompt the operator for "Your name"
        """, "Read-Host -Prompt 'Your name'")]
    [InlineData("""
        the folder is "C:/work"
        switch to the folder
        """, "Set-Location -Path $folder")]
    [InlineData("""
        the folder is "C:/work"
        cd to the folder
        """, "Set-Location -Path $folder")]
    [InlineData("""
        the folder is "C:/work"
        enter the folder
        """, "Set-Location -Path $folder")]
    [InlineData("""
        the folder is "C:/work"
        move to the folder
        """, "Set-Location -Path $folder")]
    [InlineData("""
        Allow writing files

        the line is "hello"
        the file is "a.txt"
        append the line onto the file
        """, "Add-Content -Path $file -Value $line")]
    [InlineData("""
        Allow writing files

        the text is "hello"
        the file is "a.txt"
        save the text into the file
        """, "Set-Content -Path $file -Value $text")]
    [InlineData("""
        Allow writing files

        the text is "hello"
        the file is "a.txt"
        overwrite the file with the text
        """, "Set-Content -Path $file -Value $text")]
    [InlineData("""
        Allow writing files

        the file is "a.txt"
        the destination is "b.txt"
        copy the file to the destination
        """, "Copy-Item -Path $file -Destination $destination")]
    [InlineData("""
        Allow writing files

        the file is "a.txt"
        the destination is "b.txt"
        relocate the file to the destination
        """, "Move-Item -Path $file -Destination $destination")]
    [InlineData("""
        Allow writing files

        the file is "a.txt"
        the newname is "b.txt"
        retitle the file as the newname
        """, "Rename-Item -Path $file -NewName $newname")]
    [InlineData("""
        Allow writing files

        the file is "a.txt"
        delete the file
        """, "Remove-Item -Path $file")]
    [InlineData("""
        Allow writing files

        the folder is "C:/work"
        remove the folder and everything in it
        """, "Remove-Item -Path $folder -Recurse")]
    [InlineData("""
        Allow writing files

        the folder is "C:/work"
        create the folder
        """, "New-Item -Path $folder -ItemType 'Directory' -Force")]
    [InlineData("""
        Allow writing files

        make the file "notes.txt"
        """, "New-Item -Path 'notes.txt' -ItemType 'File' -Force")]
    [InlineData("""
        the folder is "C:/work"
        the name is "a.txt"
        the joined path of the folder and the name
        """, "Join-Path -Path $folder -ChildPath $name")]
    [InlineData("""
        the folder is "C:/work"
        the name is "a.txt"
        the path joining the folder and the name
        """, "Join-Path -Path $folder -ChildPath $name")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the parent directory of the path
        """, "Split-Path -Path $path -Parent")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the containing folder of the path
        """, "Split-Path -Path $path -Parent")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the filename of the path
        """, "Split-Path -Path $path -Leaf")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the base name of the path
        """, "Split-Path -Path $path -Leaf")]
    [InlineData("""
        the path is "C:/work/a.txt"
        the leaf of the path
        """, "Split-Path -Path $path -Leaf")]
    [InlineData("""
        Allow reading files

        the file is "C:/work/a.txt"
        the absolute is the absolute path of the file
        """, "Resolve-Path -Path $file")]
    [InlineData("""
        Allow changing processes

        the app is "notepad.exe"
        launch the app
        """, "Start-Process -FilePath $app")]
    [InlineData("""
        Allow changing processes

        open the program "notepad.exe"
        """, "Start-Process -FilePath 'notepad.exe'")]
    [InlineData("""
        Allow changing processes

        the process is "notepad"
        kill the process
        """, "Stop-Process -Name $process")]
    [InlineData("""
        Allow changing processes

        the daemon is "spooler"
        halt the daemon
        """, "Stop-Service -Name $daemon")]
    public void Console_and_file_sentences_bind_powershell_commands(string source, string snippet)
    {
        var result = new CompilationPipeline().Compile(source, "gap.writ");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(snippet, result.GeneratedPowerShell);
    }

    [Theory]
    [InlineData("""
        the file is "a.txt"
        the destination is "b.txt"
        move the file to the destination
        """, "Move-Item", "Allow writing files")]
    [InlineData("""
        the file is "C:/work/a.txt"
        the full path of the file
        """, "Resolve-Path", "Allow reading files")]
    [InlineData("""
        the service is "spooler"
        stop the service
        """, "Stop-Service", "Allow changing processes")]
    public void New_commands_without_permission_fail_with_E4003(string source, string command, string allow)
    {
        var result = new CompilationPipeline().Compile(source, "gap.writ");
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E4003");
        Assert.Contains(command, diagnostic.Message);
        Assert.Contains(allow, diagnostic.Message);
    }

    [Fact]
    public void File_commands_run_only_when_the_script_runs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var writFolder = folder.Replace('\\', '/');
            var source = $$"""
                Allow writing files

                When run, returning a string
                    the folder is "{{writFolder}}"
                    the name is "note.txt"
                    the line is "hello"
                    the moved is "moved.txt"
                    the renamed is "final.txt"
                    the file is the combined path of the folder and the name
                    the destination is the combined path of the folder and the moved
                    add the line to the file
                    move the file to the destination
                    rename the destination to the renamed
                    the parent is the parent folder of the destination
                    the leaf is the file name of the destination
                    go to the folder
                    wait 0 seconds
                    warn "ready"
                    return the renamed
                """;
            var result = new CompilationPipeline().Compile(source, "gap.writ");
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("Add-Content -Path $file -Value $line", result.GeneratedPowerShell);
            Assert.Contains("Move-Item -Path $file -Destination $destination", result.GeneratedPowerShell);
            Assert.Contains("Rename-Item -Path $destination -NewName $renamed", result.GeneratedPowerShell);
            Assert.False(File.Exists(Path.Combine(folder, "note.txt")));

            var exec = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "gap", "Main", null);
            Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
            Assert.Equal("final.txt", exec.Value);
            Assert.Contains("hello", File.ReadAllText(Path.Combine(folder, "final.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "note.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "moved.txt")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
