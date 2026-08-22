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

function Invoke-AnalyticsRange {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][DateTime]$Start,
        [Parameter(Mandatory = $true)][DateTime]$End,
        [Parameter(Mandatory = $true)][string]$ManagementKey
    )

    $query = [ordered]@{
        metrics = @('request_count')
        dimensions = @('model', 'variant')
        granularity = 'day'
        time_range = [ordered]@{
            start = $Start.ToString('yyyy-MM-ddTHH:mm:ssZ')
            end = $End.ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        limit = 500
    }

    Write-Host ('=== {0} ===' -f $Label)
    Write-Host 'POST /api/v1/analytics/query'
    Write-Host ('- range: {0} -> {1}' -f $query.time_range.start, $query.time_range.end)
    Write-Host '- metric: request_count'
    Write-Host '- dimensions: model, variant'
    Write-Host '- granularity: day'
    Write-Host ''

    $response = Invoke-OpenRouterPostJson `
        -Url 'https://openrouter.ai/api/v1/analytics/query' `
        -ApiKey $ManagementKey `
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
        return $false
    }

    $json = $response.Body | ConvertFrom-Json
    $topLevelFields = @($json.PSObject.Properties.Name)
    Write-Host '[PASS] Analytics query succeeded.'
    Write-Host ('- top-level fields: {0}' -f ($topLevelFields -join ', '))

    if ($null -eq $json.data) {
        Write-Host '[WARN] Response has no data field.'
        Write-Host ''
        return $true
    }

    $rowCount = $null
    if ($null -ne $json.data.metadata -and $null -ne $json.data.metadata.row_count) {
        $rowCount = [int]$json.data.metadata.row_count
        Write-Host ('- row_count: {0}' -f $rowCount)
    }

    Write-Host ''
    Write-Host 'SANITIZED AGGREGATE DATA'
    Write-Host 'Only the requested date/model/variant/request_count aggregates are shown below.'
    $json.data | ConvertTo-Json -Depth 10

    if ($rowCount -eq 0) {
        Write-Host '[INFO] No requests were recorded in this range.'
    }

    Write-Host ''
    return $true
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
    $end = $now.UtcDateTime.AddMinutes(1)
    $recentStart = $now.UtcDateTime.Date.AddDays(-6)
    $monthStart = [DateTime]::SpecifyKind(
        [DateTime]::new($now.Year, $now.Month, 1, 0, 0, 0),
        [DateTimeKind]::Utc)

    $recentOk = Invoke-AnalyticsRange `
        -Label 'RECENT 7 DAYS' `
        -Start $recentStart `
        -End $end `
        -ManagementKey $managementKey

    $monthOk = Invoke-AnalyticsRange `
        -Label 'CURRENT MONTH' `
        -Start $monthStart `
        -End $end `
        -ManagementKey $managementKey

    Write-Host 'PROBE COMPLETE'
    Write-Host 'Copy the output above back for analysis. Do not paste your Management Key.'

    if (-not $recentOk -or -not $monthOk) {
        exit 1
    }
}
finally {
    $managementKey = $null
    $secureManagementKey = $null
}
