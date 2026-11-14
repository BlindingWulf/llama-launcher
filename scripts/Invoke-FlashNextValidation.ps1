[CmdletBinding()]
param(
    [string]$BaseUri = 'http://127.0.0.1:18090',
    [string]$Model = 'qwen-flash-next',
    [string]$ApiKey = '',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\.private\flashnext-validation.json'),
    [ValidateSet('off', 'low', 'medium', 'xhigh')]
    [string]$ReasoningEffort = 'off',
    [int]$MaxTokens = 1024
)

$ErrorActionPreference = 'Stop'
$headers = @{}
if ($ApiKey) { $headers.Authorization = "Bearer $ApiKey" }

$cases = @(
    [ordered]@{
        id = 'sequence'
        prompt = 'Return the integers 1 through 40, separated only by single commas. Do not add spaces or commentary.'
        expected = (1..40 -join ',')
    },
    [ordered]@{
        id = 'strict-json'
        prompt = 'Return a single minified JSON object with exactly the keys status, count, and items, in that order; no Markdown or commentary. Values must be "ok", 3, and ["alpha","beta","gamma"].'
        expected = '{"status":"ok","count":3,"items":["alpha","beta","gamma"]}'
    },
    [ordered]@{
        id = 'arithmetic'
        prompt = 'A warehouse begins with 2,400 parts. It ships 18% of them, receives 332, then discards 7% of the resulting stock. How many whole parts remain after the discard? Show the intermediate integer counts.'
        expectedContains = @('1,968', '2,300', '161', '2,139')
    },
    [ordered]@{
        id = 'generation-sanity'
        prompt = 'In exactly four short bullet points, explain why memory-mapped model weights can consume physical RAM through the operating-system file cache.'
        expectedBulletCount = 4
    }
)

$results = foreach ($case in $cases) {
    $body = [ordered]@{
        model = $Model
        temperature = 0
        max_tokens = $MaxTokens
        messages = @([ordered]@{ role = 'user'; content = $case.prompt })
    }
    if ($ReasoningEffort -ne 'off') { $body.reasoning_effort = $ReasoningEffort }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod -Method Post -Uri "$BaseUri/v1/chat/completions" -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8)
        $clock.Stop()
        $content = [string]$response.choices[0].message.content
        $passed = $true
        if ($case.expected) { $passed = $content.Trim() -ceq $case.expected }
        if ($case.expectedContains) {
            $passed = @($case.expectedContains | Where-Object { !$content.Contains($_) }).Count -eq 0
        }
        if ($case.expectedBulletCount) {
            $passed = @($content -split "`r?`n" | Where-Object { $_ -match '^\s*[-*]\s+' }).Count -eq $case.expectedBulletCount
        }
        [ordered]@{
            id = $case.id
            passed = $passed
            elapsed_ms = $clock.ElapsedMilliseconds
            prompt_tokens = $response.usage.prompt_tokens
            completion_tokens = $response.usage.completion_tokens
            content = $content
        }
    } catch {
        $clock.Stop()
        [ordered]@{ id = $case.id; passed = $false; elapsed_ms = $clock.ElapsedMilliseconds; error = $_.Exception.Message }
    }
}

$document = [ordered]@{
    schema = 1
    captured_at = [DateTimeOffset]::Now.ToString('o')
    base_uri = $BaseUri
    model = $Model
    reasoning_effort = $ReasoningEffort
    max_tokens = $MaxTokens
    passed = @($results | Where-Object passed).Count
    total = $results.Count
    results = $results
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$document | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$document
if ($document.passed -ne $document.total) { exit 1 }
