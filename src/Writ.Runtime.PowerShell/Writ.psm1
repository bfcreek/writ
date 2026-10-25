# Writ runtime module for generated PowerShell scripts.
# The compiler never imports this module. It is loaded only when a program runs.
Set-StrictMode -Version Latest

function New-IntentList {
    param($Like)
    if ($null -ne $Like -and $Like -is [System.Collections.IList] -and $Like.GetType().IsGenericType) {
        return ,[System.Activator]::CreateInstance($Like.GetType())
    }
    return ,[System.Collections.Generic.List[object]]::new()
}

function Get-IntentLength {
    param($Value)
    if ($null -eq $Value) { return [long]0 }
    if ($Value -is [string]) { return [long]$Value.Length }
    return [long]@($Value).Count
}

function Get-IntentRounded {
    param($Value, $Digits)
    return [math]::Round([decimal]$Value, [int]$Digits, [System.MidpointRounding]::AwayFromZero)
}

function Get-IntentSum {
    param($Values)
    $total = [decimal]0
    foreach ($v in @($Values)) { $total += [decimal]$v }
    return $total
}

function Add-IntentItem {
    param($List, $Item)
    $new = New-IntentList -Like $List
    foreach ($existing in @($List)) { [void]$new.Add($existing) }
    [void]$new.Add($Item)
    return ,$new
}

function ConvertTo-IntentString {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [bool]) { if ($Value) { return 'True' } else { return 'False' } }
    return [string]$Value
}

function ConvertTo-IntentInteger {
    param($Value)
    if ($Value -is [bool]) { if ($Value) { return [long]1 } else { return [long]0 } }
    if ($Value -is [string]) { return [long]::Parse($Value, [cultureinfo]::InvariantCulture) }
    return [long][math]::Truncate([decimal]$Value)
}

function ConvertTo-IntentDecimal {
    param($Value)
    if ($Value -is [string]) { return [decimal]::Parse($Value, [cultureinfo]::InvariantCulture) }
    return [decimal]$Value
}

function ConvertTo-IntentBoolean {
    param($Value)
    if ($Value -is [string]) { return [bool]::Parse($Value) }
    return [bool]$Value
}

function Get-IntentSumMap {
    param($Items, [scriptblock]$Projection)
    $total = [decimal]0
    foreach ($item in @($Items)) { $total += [decimal](& $Projection $item) }
    return $total
}

function Select-IntentWhere {
    param($Items, [scriptblock]$Predicate)
    $result = New-IntentList -Like $Items
    foreach ($item in @($Items)) { if (& $Predicate $item) { [void]$result.Add($item) } }
    return ,$result
}

function Assert-IntentField {
    param($Value, [string]$Field, [string]$TypeName)
    if ($Value -is [System.Collections.IDictionary]) {
        if (-not $Value.Contains($Field)) { throw "Expected a '$Field' field to read a $TypeName, and it is missing." }
        return
    }
    if ($null -eq $Value.PSObject.Properties[$Field]) {
        throw "Expected a '$Field' field to read a $TypeName, and it is missing."
    }
}

function Write-IntentCallLog {
    param([string]$Entry)
    if ($env:INTENT_CALL_LOG) { Add-Content -LiteralPath $env:INTENT_CALL_LOG -Value $Entry }
}

$script:IntentMcpSessions = @{}
$script:IntentMcpRestSessions = @{}
$script:IntentMcpRestNextId = 2
$script:IntentMcpValidated = @{}

function Split-IntentCommandLine([string]$CommandLine) {
    $trimmed = $CommandLine.Trim()
    if ($trimmed.StartsWith('"')) {
        $close = $trimmed.IndexOf('"', 1)
        if ($close -gt 0) { return @($trimmed.Substring(1, $close - 1), $trimmed.Substring($close + 1).Trim()) }
    }
    $space = $trimmed.IndexOf(' ')
    if ($space -lt 0) { return @($trimmed, '') }
    return @($trimmed.Substring(0, $space), $trimmed.Substring($space + 1).Trim())
}

function Send-IntentMcp($Session, $Message) {
    $json = ConvertTo-Json -InputObject $Message -Depth 32 -Compress
    $Session.Process.StandardInput.WriteLine($json)
    $Session.Process.StandardInput.Flush()
}

function Receive-IntentMcp($Session, [int]$Id) {
    while ($true) {
        $line = $Session.Process.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "The MCP server '$($Session.CommandLine)' closed its output." }
        if (-not $line.Trim().StartsWith('{')) { continue }
        $msg = ConvertFrom-Json -InputObject $line -AsHashtable
        if (-not $msg.ContainsKey('id') -or $msg.id -ne $Id) { continue }
        if ($msg.ContainsKey('error')) { throw "MCP server returned an error: $($msg.error.message)" }
        return $msg.result
    }
}

function Get-IntentMcpSession([string]$CommandLine, [string]$WorkingDirectory) {
    $key = "$CommandLine`n$WorkingDirectory"
    if ($script:IntentMcpSessions.ContainsKey($key)) { return $script:IntentMcpSessions[$key] }

    $parts = Split-IntentCommandLine $CommandLine
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $parts[0]
    $psi.Arguments = $parts[1]
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) { throw "Could not start MCP server '$CommandLine'." }
    $null = $process.StandardError.ReadToEndAsync()

    $session = [pscustomobject]@{ Process = $process; CommandLine = $CommandLine; NextId = 2 }
    Send-IntentMcp $session @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
        protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'writ-powershell'; version = '0.1.0' } } }
    $null = Receive-IntentMcp $session 1
    Send-IntentMcp $session @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $script:IntentMcpSessions[$key] = $session
    return $session
}

function Invoke-WritMcpTool {
    param([string]$CommandLine, [string]$WorkingDirectory, [string]$Tool, [hashtable]$Arguments)
    $session = Get-IntentMcpSession -CommandLine $CommandLine -WorkingDirectory $WorkingDirectory
    $id = $session.NextId
    $session.NextId = $id + 1
    if ($null -eq $Arguments) { $Arguments = @{} }
    Send-IntentMcp $session @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments } }
    $result = Receive-IntentMcp $session $id

    $text = $null
    if ($result.ContainsKey('content')) {
        foreach ($item in $result.content) {
            if ($item.type -eq 'text') { $text = [string]$item.text; break }
        }
    }
    if ($result.ContainsKey('isError') -and $result.isError) {
        if ($null -eq $text) { $text = "MCP tool '$Tool' failed." }
        throw $text
    }
    if ($result.ContainsKey('structuredContent')) {
        return ConvertTo-IntentData $result.structuredContent
    }
    if ($null -eq $text) { return ConvertTo-IntentData $result }
    try {
        $parsed = ConvertFrom-Json -InputObject $text -NoEnumerate
        if ($parsed -is [System.Collections.IList]) { return ,$parsed }
        return $parsed
    }
    catch {
        return $text
    }
}

function Send-IntentMcpRest {
    param([string]$Url, [string]$SessionId, $Message)
    $headers = @{ Accept = 'application/json, text/event-stream' }
    if ($SessionId) { $headers['Mcp-Session-Id'] = $SessionId }
    $body = ConvertTo-Json -InputObject $Message -Depth 32 -Compress
    $response = Invoke-WebRequest -Uri $Url -Method Post -ContentType 'application/json' -Headers $headers -Body $body -SkipHttpErrorCheck
    $session = $SessionId
    foreach ($key in @($response.Headers.Keys)) {
        if ("$key" -eq 'Mcp-Session-Id') {
            $raw = $response.Headers[$key]
            $session = if ($raw -is [System.Array]) { [string]$raw[0] } else { [string]$raw }
            break
        }
    }
    $payload = [string]$response.Content
    if ($null -eq $payload) { $payload = '' }
    $trimmed = $payload.Trim()
    if ($response.StatusCode -eq 202 -or $response.StatusCode -eq 204 -or $trimmed.Length -eq 0) {
        return [pscustomobject]@{ SessionId = $session; Result = $null }
    }
    if ($response.StatusCode -ge 400) { throw "MCP server returned $($response.StatusCode): $trimmed" }
    if (-not $trimmed.StartsWith('{') -and -not $trimmed.StartsWith('[')) {
        $jsonLine = $null
        foreach ($line in ($payload -split "`n")) {
            $line = $line.Trim()
            if ($line.StartsWith('data:')) {
                $candidate = $line.Substring(5).Trim()
                if ($candidate.StartsWith('{')) { $jsonLine = $candidate }
            }
        }
        if ($null -eq $jsonLine) { throw 'MCP server returned a response that is not JSON.' }
        $trimmed = $jsonLine
    }
    $msg = ConvertFrom-Json -InputObject $trimmed -AsHashtable
    if ($msg.ContainsKey('error')) { throw "MCP server returned an error: $($msg.error.message)" }
    $result = if ($msg.ContainsKey('result')) { $msg.result } else { $msg }
    return [pscustomobject]@{ SessionId = $session; Result = $result }
}

function Get-IntentMcpRestSession([string]$Url) {
    if ($script:IntentMcpRestSessions.ContainsKey($Url)) { return $script:IntentMcpRestSessions[$Url] }
    $init = Send-IntentMcpRest -Url $Url -SessionId '' -Message @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
            protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'writ-powershell'; version = '0.1.0' }
        }
    }
    $script:IntentMcpRestSessions[$Url] = [string]$init.SessionId
    $null = Send-IntentMcpRest -Url $Url -SessionId ([string]$init.SessionId) -Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    return [string]$init.SessionId
}

function Invoke-WritMcpRest {
    param([string]$Url, [string]$Tool, [hashtable]$Arguments)
    $sessionId = Get-IntentMcpRestSession -Url $Url
    if ($null -eq $Arguments) { $Arguments = @{} }
    $id = $script:IntentMcpRestNextId
    $script:IntentMcpRestNextId = $id + 1
    $call = Send-IntentMcpRest -Url $Url -SessionId $sessionId -Message @{
        jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments }
    }
    $result = $call.Result
    if ($null -eq $result) { throw "MCP tool '$Tool' returned an empty response." }

    $text = $null
    if ($result.ContainsKey('content')) {
        foreach ($item in $result.content) {
            if ($item.type -eq 'text') { $text = [string]$item.text; break }
        }
    }
    if ($result.ContainsKey('isError') -and $result.isError) {
        if ($null -eq $text) { $text = "MCP tool '$Tool' failed." }
        throw $text
    }
    if ($result.ContainsKey('structuredContent')) {
        return ConvertTo-IntentData $result.structuredContent
    }
    if ($null -eq $text) { return ConvertTo-IntentData $result }
    try {
        $parsed = ConvertFrom-Json -InputObject $text -NoEnumerate
        if ($parsed -is [System.Collections.IList]) { return ,$parsed }
        return $parsed
    }
    catch {
        return $text
    }
}

# Names offered by one server. tools/list is the only method this sends.
function Get-IntentMcpToolNames {
    param([string]$Transport, [string]$Endpoint, [string]$WorkingDirectory)
    if ($Transport -eq 'rest') {
        $sessionId = Get-IntentMcpRestSession -Url $Endpoint
        $id = $script:IntentMcpRestNextId
        $script:IntentMcpRestNextId = $id + 1
        $listed = Send-IntentMcpRest -Url $Endpoint -SessionId $sessionId -Message @{
            jsonrpc = '2.0'; id = $id; method = 'tools/list'; params = @{}
        }
        Read-IntentMcpToolNames $listed.Result
        return
    }
    $session = Get-IntentMcpSession -CommandLine $Endpoint -WorkingDirectory $WorkingDirectory
    $id = $session.NextId
    $session.NextId = $id + 1
    Send-IntentMcp $session @{ jsonrpc = '2.0'; id = $id; method = 'tools/list'; params = @{} }
    Read-IntentMcpToolNames (Receive-IntentMcp $session $id)
}

function Read-IntentMcpToolNames($Result) {
    $names = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Result) { return $names.ToArray() }
    $tools = $null
    if ($Result -is [System.Collections.IDictionary]) {
        if ($Result.Contains('tools')) { $tools = $Result['tools'] }
    }
    elseif ($null -ne $Result.PSObject.Properties['tools']) {
        $tools = $Result.tools
    }
    foreach ($tool in @($tools)) {
        if ($null -eq $tool) { continue }
        $name = $null
        if ($tool -is [System.Collections.IDictionary]) {
            if ($tool.Contains('name')) { $name = [string]$tool['name'] }
        }
        elseif ($null -ne $tool.PSObject.Properties['name']) {
            $name = [string]$tool.name
        }
        if ($name) { [void]$names.Add($name) }
    }
    return $names.ToArray()
}

# The script's required tools must still be offered. One tools/list per server, no tools/call.
function Assert-WritMcpTools {
    param([string]$RequiredJson)
    if ([string]::IsNullOrWhiteSpace($RequiredJson) -or $RequiredJson -eq '[]') { return }
    if ($script:IntentMcpValidated.ContainsKey($RequiredJson)) { return }
    $required = @(ConvertFrom-Json -InputObject $RequiredJson)
    $groups = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([System.StringComparer]::Ordinal)
    foreach ($tool in $required) {
        if ($null -eq $tool) { continue }
        $key = ([string]$tool.transport) + "`n" + ([string]$tool.endpoint) + "`n" + ([string]$tool.workingDirectory)
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = [System.Collections.Generic.List[object]]::new()
        }
        [void]$groups[$key].Add($tool)
    }
    foreach ($group in $groups.Values) {
        $first = $group[0]
        $available = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($name in @(Get-IntentMcpToolNames -Transport ([string]$first.transport) -Endpoint ([string]$first.endpoint) -WorkingDirectory ([string]$first.workingDirectory))) {
            foreach ($part in @($name)) {
                if ($part -is [string] -and $part) { [void]$available.Add($part) }
            }
        }
        foreach ($tool in $group) {
            $operation = [string]$tool.operation
            if (-not $available.Contains($operation)) {
                throw "Required MCP tool '$([string]$tool.alias).$operation' is not available."
            }
        }
    }
    $script:IntentMcpValidated[$RequiredJson] = $true
}

# Hashtables from -AsHashtable become objects so field access reads the same as JSON data.
function ConvertTo-IntentData($Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 32 -Compress
    $data = ConvertFrom-Json -InputObject $json -NoEnumerate
    if ($data -is [System.Collections.IList]) { return ,$data }
    return $data
}

# A capability failure carries where it came from, so "if that fails" handlers and callers can
# tell a tool error apart from a bug in the script: $_.Exception.Alias / .Operation / .Provider.
class WritCapabilityException : System.Exception {
    [string]$Alias
    [string]$Operation
    [string]$Provider
    WritCapabilityException([string]$message, [string]$alias, [string]$operation, [string]$provider) : base($message) {
        $this.Alias = $alias
        $this.Operation = $operation
        $this.Provider = $provider
    }
}

function Invoke-WritRetry {
    param([int]$Times, [scriptblock]$Action)
    $last = $null
    for ($attempt = 1; $attempt -le [Math]::Max(1, $Times); $attempt++) {
        try { return (& $Action) }
        catch { $last = $_ }
    }
    throw "Gave up after $Times attempts: $($last.Exception.Message)"
}

function Invoke-WritCapability {
    param(
        [string]$Alias,
        [string]$Operation,
        [string]$ManifestPath,
        [hashtable]$Arguments,
        [string]$Provider = 'mock',
        [string]$Endpoint = '',
        [string]$Transport = 'stdio',
        [string]$WorkingDirectory = '',
        [switch]$Writes
    )
    if ($Writes -and $env:INTENT_DRY_RUN) {
        Write-Host "What if: would call $Alias.$Operation with $(ConvertTo-Json -InputObject $Arguments -Compress -Depth 8)"
        # A mock result, when the manifest has one, lets the rest of the dry run proceed as if it succeeded.
        if ($Provider -eq 'mock' -and $ManifestPath -and (Test-Path -LiteralPath $ManifestPath)) {
            $dry = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
            if ($dry.ContainsKey('mock') -and $dry.mock.ContainsKey($Operation) -and $dry.mock[$Operation].ContainsKey('result')) {
                $sample = $dry.mock[$Operation].result
                if ($sample -is [System.Collections.IList]) { return ,$sample }
                return $sample
            }
        }
        return $null
    }
    Write-IntentCallLog -Entry "$Alias.$Operation"
    try {
        if ($Provider -eq 'mcp') {
            if ($Transport -eq 'rest') {
                return Invoke-WritMcpRest -Url $Endpoint -Tool $Operation -Arguments $Arguments
            }
            return Invoke-WritMcpTool -CommandLine $Endpoint -WorkingDirectory $WorkingDirectory -Tool $Operation -Arguments $Arguments
        }
        if ($Provider -ne 'mock') {
            throw "Provider '$Provider' for '$Alias' is not supported yet."
        }
        if (-not (Test-Path -LiteralPath $ManifestPath)) {
            throw "Manifest for '$Alias' was not found at '$ManifestPath'."
        }
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
        if (-not $manifest.ContainsKey('mock') -or -not $manifest.mock.ContainsKey($Operation)) {
            throw "No mock result is declared for $Alias.$Operation."
        }
        $entry = $manifest.mock[$Operation]
        if ($entry.ContainsKey('failure')) { throw [string]$entry.failure }
        if (-not $entry.ContainsKey('result')) { throw "Mock for $Alias.$Operation has no result." }
        $result = $entry.result
        if ($result -is [System.Collections.IList]) { return ,$result }
        return $result
    }
    catch {
        $message = if ($_.Exception -is [WritCapabilityException]) { $_.Exception.Message } else { "$Alias.$Operation failed: $($_.Exception.Message)" }
        throw [WritCapabilityException]::new($message, $Alias, $Operation, $Provider)
    }
}

function Invoke-WritScript {
    param(
        [string]$Path,
        [string]$Kind,
        [string]$Compiled = '',
        [hashtable]$Arguments = @{},
        [switch]$Writes
    )
    if ($Writes -and $env:INTENT_DRY_RUN) {
        Write-Host "What if: would run $Path"
        return $null
    }
    Write-IntentCallLog -Entry ("script:" + [System.IO.Path]::GetFileName($Path))
    if ($Kind -eq 'writ') {
        if (-not $Compiled) { throw "No compiled script was provided for '$Path'." }
        return Invoke-WritCompiledScript -Compiled $Compiled -Arguments $Arguments -Path $Path
    }
    return Invoke-WritPowerShellFile -Path $Path -Arguments $Arguments
}

function Invoke-WritPowerShellFile {
    param([string]$Path, [hashtable]$Arguments)
    $psi = New-IntentPwsh -WorkingDirectory ([System.IO.Path]::GetDirectoryName($Path))
    $psi.ArgumentList.Add('-File')
    $psi.ArgumentList.Add($Path)
    foreach ($key in @($Arguments.Keys)) {
        $psi.ArgumentList.Add("-$key")
        $value = $Arguments[$key]
        if ($null -eq $value) { continue }
        if ($value -is [bool]) {
            $psi.ArgumentList.Add($(if ($value) { 'true' } else { 'false' }))
        } else {
            $psi.ArgumentList.Add([string]$value)
        }
    }
    $done = Wait-IntentProcess $psi
    if ($done.ExitCode -ne 0) { throw "Script '$Path' failed ($($done.ExitCode)). $($done.Stderr)".Trim() }
    return $done.Stdout.Trim()
}

function Invoke-WritCompiledScript {
    param([string]$Compiled, [hashtable]$Arguments, [string]$Path)
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("writ-call-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Writ.psm1') -Destination (Join-Path $dir 'Writ.psm1')
        $scriptPath = Join-Path $dir 'callee.ps1'
        $inputPath = Join-Path $dir 'input.json'
        $runnerPath = Join-Path $dir 'runner.ps1'
        Set-Content -LiteralPath $scriptPath -Value $Compiled -Encoding utf8
        $payload = @{}
        foreach ($key in @($Arguments.Keys)) { $payload[$key] = $Arguments[$key] }
        Set-Content -LiteralPath $inputPath -Value (ConvertTo-Json -InputObject $payload -Depth 32 -Compress) -Encoding utf8
        $runner = @(
            'param([string]$ScriptPath, [string]$InputPath)'
            '$ErrorActionPreference = ''Stop'''
            '. $ScriptPath'
            '__Assert-WritMcpTools'
            '$__raw = Get-Content -LiteralPath $InputPath -Raw'
            '$__input = if ([string]::IsNullOrWhiteSpace($__raw)) { [pscustomobject]@{} } else { $__raw | ConvertFrom-Json }'
            '$__params = @{}'
            'foreach ($__p in @($__input.PSObject.Properties)) { $__params[$__p.Name] = $__p.Value }'
            '$__result = & Main @__params'
            'Write-Output ''__INTENT_RESULT__'''
            'if ($null -eq $__result) { Write-Output ''null'' } else { Write-Output (ConvertTo-Json -InputObject $__result -Depth 32 -Compress) }'
        ) -join "`n"
        Set-Content -LiteralPath $runnerPath -Value $runner -Encoding utf8
        $psi = New-IntentPwsh -WorkingDirectory $dir
        $psi.ArgumentList.Add('-File')
        $psi.ArgumentList.Add($runnerPath)
        $psi.ArgumentList.Add('-ScriptPath')
        $psi.ArgumentList.Add($scriptPath)
        $psi.ArgumentList.Add('-InputPath')
        $psi.ArgumentList.Add($inputPath)
        $done = Wait-IntentProcess $psi
        if ($done.ExitCode -ne 0) { throw "Script '$Path' failed ($($done.ExitCode)). $($done.Stderr) $($done.Stdout)".Trim() }
        return ConvertFrom-IntentScriptResult $done.Stdout
    }
    finally {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function ConvertFrom-IntentScriptResult([string]$Stdout) {
    $marker = '__INTENT_RESULT__'
    $index = $Stdout.LastIndexOf($marker)
    if ($index -lt 0) { throw "Script did not return a result. $Stdout" }
    $json = $Stdout.Substring($index + $marker.Length).Trim()
    if ($json -eq '' -or $json -eq 'null') { return $null }
    $parsed = ConvertFrom-Json -InputObject $json -NoEnumerate
    if ($null -ne $parsed -and $parsed -is [System.Collections.IList] -and -not ($parsed -is [string])) { return ,$parsed }
    return $parsed
}

function New-IntentPwsh([string]$WorkingDirectory) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new('pwsh')
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    foreach ($arg in @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass')) {
        $psi.ArgumentList.Add($arg)
    }
    return $psi
}

function Wait-IntentProcess([System.Diagnostics.ProcessStartInfo]$StartInfo) {
    $process = [System.Diagnostics.Process]::Start($StartInfo)
    if ($null -eq $process) { throw "Could not start pwsh." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) {
        try { $process.Kill($true) } catch { }
        throw "Script did not finish within 120 seconds."
    }
    $null = $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

Export-ModuleMember -Function *
