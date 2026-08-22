param()

$ErrorActionPreference = 'Stop'

Write-Host 'QuotaTray OpenRouter analytics probe'
Write-Host 'Queries sanitized aggregate usage only; no API key or prompt content is printed.'
Write-Host ''

function Get-PlainTextFromSecureString {
    param([Parameter(Mandatory = $true)][Security.SecureString]$Secure)

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Invoke-OpenRouterPostJson {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)]$Body
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)

    try {
        $json = $Body | ConvertTo-Json -Depth 8 -Compress
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Url)
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $ApiKey)
        $request.Headers.UserAgent.ParseAdd('QuotaTray-OpenRouter-Analytics-Probe/1.0')
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Reason     = $response.ReasonPhrase
            Body       = $body
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

$managementKey = $env:OPENROUTER_MANAGEMENT_KEY
if ([string]::IsNullOrWhiteSpace($managementKey)) {
    $secureManagementKey = Read-Host 'OpenRouter Management key' -AsSecureString
    $managementKey = Get-PlainTextFromSecureString -Secure $secureManagementKey
}

if ([string]::IsNullOrWhiteSpace($managementKey)) {
    Write-Error 'No OpenRouter Management Key provided.'
    exit 2
}

try {
    $now = [DateTimeOffset]::UtcNow
    $start = $now.UtcDateTime.Date.AddDays(-6)
    $end = $now.UtcDateTime.AddMinutes(1)

    $query = [ordered]@{
        metrics = @('request_count')
        dimensions = @('model', 'variant')
        granularity = 'day'
        time_range = [ordered]@{
            start = $start.ToString('yyyy-MM-ddTHH:mm:ssZ')
            end = $end.ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        limit = 200
    }

    Write-Host 'POST /api/v1/analytics/query'
    Write-Host ('- range: {0} -> {1}' -f $query.time_range.start, $query.time_range.end)
    Write-Host '- metric: request_count'
    Write-Host '- dimensions: model, variant'
    Write-Host '- granularity: day'
    Write-Host ''

    $response = Invoke-OpenRouterPostJson `
        -Url 'https://openrouter.ai/api/v1/analytics/query' `
        -ApiKey $managementKey `
        -Body $query

    Write-Host ('HTTP {0} {1}' -f $response.StatusCode, $response.Reason)

    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        Write-Host '[FAIL] Analytics query failed.'
        try {
            $errorJson = $response.Body | ConvertFrom-Json
            if ($null -ne $errorJson.error) {
                Write-Host ('- error: {0}' -f $errorJson.error)
            }
            elseif ($null -ne $errorJson.message) {
                Write-Host ('- message: {0}' -f $errorJson.message)
            }
        }
        catch {
            # Keep response body private/unprinted on parse failures.
        }
        exit 1
    }

    $json = $response.Body | ConvertFrom-Json
    $topLevelFields = @($json.PSObject.Properties.Name)
    Write-Host '[PASS] Analytics query succeeded.'
    Write-Host ('- top-level fields: {0}' -f ($topLevelFields -join ', '))

    if ($null -eq $json.data) {
        Write-Host '[WARN] Response has no data field.'
        exit 0
    }

    Write-Host ''
    Write-Host 'SANITIZED AGGREGATE DATA'
    Write-Host 'Only the requested date/model/variant/request_count aggregates are shown below.'
    $json.data | ConvertTo-Json -Depth 10

    Write-Host ''
    Write-Host 'PROBE COMPLETE'
    Write-Host 'Copy the output above back for analysis. Do not paste your Management Key.'
}
finally {
    $managementKey = $null
    $secureManagementKey = $null
}
