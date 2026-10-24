# A tiny MCP server over stdio (newline-delimited JSON-RPC 2.0) used by Writ tests and samples.
# It offers two tools and logs every JSON-RPC method it receives to $env:INTENT_MCP_LOG, so tests can
# prove that `writ check` only discovers (initialize, tools/list) and never calls a tool.
$ErrorActionPreference = 'Stop'
$log = $env:INTENT_MCP_LOG

function Send-Message($obj) {
    $json = ConvertTo-Json -InputObject $obj -Depth 16 -Compress
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

function Write-Log($method) {
    if ($log) { Add-Content -LiteralPath $log -Value $method }
}

$tools = @(
    @{
        name = 'list_failed'
        description = 'List payments that failed on a day.'
        inputSchema = @{ type = 'object'; properties = @{ day = @{ type = 'string'; description = 'ISO date' } }; required = @('day') }
        annotations = @{ readOnlyHint = $true }
    },
    @{
        name = 'retry'
        description = 'Retry one failed payment.'
        inputSchema = @{ type = 'object'; properties = @{ paymentId = @{ type = 'string' } }; required = @('paymentId') }
        annotations = @{ readOnlyHint = $false; idempotentHint = $false }
    }
)

$payments = @(
    @{ id = 'p1'; retryCount = 1 },
    @{ id = 'p2'; retryCount = 5 }
)

while ($null -ne ($line = [Console]::In.ReadLine())) {
    if (-not $line.Trim()) { continue }
    $msg = ConvertFrom-Json -InputObject $line -AsHashtable
    $method = [string]$msg.method
    Write-Log $method
    if (-not $msg.ContainsKey('id')) { continue }

    switch ($method) {
        'initialize' {
            Send-Message @{ jsonrpc = '2.0'; id = $msg.id; result = @{
                protocolVersion = '2025-06-18'
                capabilities = @{ tools = @{} }
                serverInfo = @{ name = 'writ-test-server'; version = '0.1.0' } } }
        }
        'ping' { Send-Message @{ jsonrpc = '2.0'; id = $msg.id; result = @{} } }
        'tools/list' { Send-Message @{ jsonrpc = '2.0'; id = $msg.id; result = @{ tools = $tools } } }
        'tools/call' {
            $name = [string]$msg.params.name
            $args = $msg.params.arguments
            if ($null -eq $args) { $args = @{} }
            switch ($name) {
                'list_failed' {
                    $text = ConvertTo-Json -InputObject $payments -Depth 8 -Compress
                    Send-Message @{ jsonrpc = '2.0'; id = $msg.id; result = @{ content = @(@{ type = 'text'; text = $text }) } }
                }
                'retry' {
                    $paymentId = [string]$args.paymentId
                    if ($paymentId -eq 'bad') {
                        Send-Message @{ jsonrpc = '2.0'; id = $msg.id; result = @{ isError = $true; content = @(@{ type = 'text'; text = "Payment '$paymentId' was not found." }) } }
                    }
                    else {
                        $structured = @{ succeeded = $true; errorMessage = '' }
                        Send-Message @{ jsonrpc = '2.0'; id = $msg.id; result = @{
                            content = @(@{ type = 'text'; text = (ConvertTo-Json -InputObject $structured -Compress) })
                            structuredContent = $structured } }
                    }
                }
                default {
                    Send-Message @{ jsonrpc = '2.0'; id = $msg.id; error = @{ code = -32602; message = "Unknown tool: $name" } }
                }
            }
        }
        default {
            Send-Message @{ jsonrpc = '2.0'; id = $msg.id; error = @{ code = -32601; message = "Method not found: $method" } }
        }
    }
}
