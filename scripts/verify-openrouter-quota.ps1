param()

$ErrorActionPreference = 'Stop'

Write-Host 'QuotaTray OpenRouter quota probe'
Write-Host 'Queries current OpenRouter endpoints and prints only quota-relevant fields.'
Write-Host 'API keys themselves are never printed.'
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

function Invoke-OpenRouterGet {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$ApiKey
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(20)

    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $ApiKey)
        $request.Headers.UserAgent.ParseAdd('QuotaTray-OpenRouter-Verifier/1.0')

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

function Read-JsonSafe {
    param([string]$Text)
    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Write-Field {
    param(
        [string]$Name,
        $Value
    )

    if ($null -eq $Value) {
        Write-Host ('- {0}: <not returned>' -f $Name)
    }
    elseif ($Value -is [bool]) {
        Write-Host ('- {0}: {1}' -f $Name, $Value.ToString().ToLowerInvariant())
    }
    else {
        Write-Host ('- {0}: {1}' -f $Name, $Value)
    }
}

function Write-AnalyticsMetaList {
    param(
        [string]$Name,
        $Value
    )

    if ($null -eq $Value) {
        Write-Host ('- {0}: <not returned>' -f $Name)
        return
    }

    $entries = @($Value)
    if ($entries.Count -eq 0) {
        Write-Host ('- {0}: <empty>' -f $Name)
        return
    }

    $labels = foreach ($entry in $entries) {
        if ($null -eq $entry) { continue }

        if ($entry -is [string] -or $entry -is [ValueType]) {
            [string]$entry
            continue
        }

        $preferred = @('name', 'id', 'key', 'value', 'metric', 'dimension', 'granularity')
        $label = $null
        foreach ($propertyName in $preferred) {
            $property = $entry.PSObject.Properties[$propertyName]
            if ($null -ne $property -and $null -ne $property.Value -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                $label = [string]$property.Value
                break
            }
        }

        if ([string]::IsNullOrWhiteSpace($label)) {
            $label = ($entry | ConvertTo-Json -Compress -Depth 5)
        }
        $label
    }

    if ($labels.Count -gt 50) {
        $visible = $labels | Select-Object -First 50
        Write-Host ('- {0} ({1}): {2}, ...' -f $Name, $labels.Count, ($visible -join ', '))
    }
    else {
        Write-Host ('- {0} ({1}): {2}' -f $Name, $labels.Count, ($labels -join ', '))
    }
}

$apiKey = $env:OPENROUTER_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $secureApiKey = Read-Host 'OpenRouter API key' -AsSecureString
    $apiKey = Get-PlainTextFromSecureString -Secure $secureApiKey
}

if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Error 'No OpenRouter API key provided.'
    exit 2
}

$managementKey = $env:OPENROUTER_MANAGEMENT_KEY
if ([string]::IsNullOrWhiteSpace($managementKey)) {
    Write-Host 'Management Key is optional and is used only for Analytics.'
    $secureManagementKey = Read-Host 'OpenRouter Management key (press Enter to skip)' -AsSecureString
    $managementKey = Get-PlainTextFromSecureString -Secure $secureManagementKey
}

try {
    Write-Host ''
    Write-Host '[1/3] REGULAR KEY -> GET /api/v1/key'
    $keyResponse = Invoke-OpenRouterGet -Url 'https://openrouter.ai/api/v1/key' -ApiKey $apiKey
    Write-Host ('HTTP {0} {1}' -f $keyResponse.StatusCode, $keyResponse.Reason)

    if ($keyResponse.StatusCode -ge 200 -and $keyResponse.StatusCode -lt 300) {
        $keyJson = Read-JsonSafe $keyResponse.Body
        $data = $keyJson.data

        if ($null -eq $data) {
            Write-Host '[WARN] Response has no data object.'
        }
        else {
            Write-Field 'is_free_tier' $data.is_free_tier
            Write-Field 'limit' $data.limit
            Write-Field 'limit_remaining' $data.limit_remaining
            Write-Field 'limit_reset' $data.limit_reset
            Write-Field 'usage' $data.usage
            Write-Field 'usage_daily' $data.usage_daily
            Write-Field 'usage_weekly' $data.usage_weekly
            Write-Field 'usage_monthly' $data.usage_monthly
            Write-Field 'expires_at' $data.expires_at
        }
    }
    else {
        Write-Host '[WARN] /key is not available for this regular API key.'
    }

    Write-Host ''
    Write-Host '[2/3] REGULAR KEY -> GET /api/v1/credits'
    $creditsResponse = Invoke-OpenRouterGet -Url 'https://openrouter.ai/api/v1/credits' -ApiKey $apiKey
    Write-Host ('HTTP {0} {1}' -f $creditsResponse.StatusCode, $creditsResponse.Reason)

    if ($creditsResponse.StatusCode -ge 200 -and $creditsResponse.StatusCode -lt 300) {
        $creditsJson = Read-JsonSafe $creditsResponse.Body
        $credits = $creditsJson.data

        if ($null -eq $credits) {
            Write-Host '[WARN] Response has no data object.'
        }
        else {
            $totalCredits = $credits.total_credits
            $totalUsage = $credits.total_usage
            Write-Field 'total_credits' $totalCredits
            Write-Field 'total_usage' $totalUsage

            if ($null -ne $totalCredits -and $null -ne $totalUsage) {
                $balance = [Math]::Max(0.0, [double]$totalCredits - [double]$totalUsage)
                Write-Host ('- computed_balance: {0:N4}' -f $balance)
            }
        }
    }
    else {
        Write-Host '[INFO] /credits is unavailable for this regular API key; provider should treat it as optional.'
    }

    Write-Host ''
    Write-Host '[3/3] MANAGEMENT KEY -> GET /api/v1/analytics/meta'

    if ([string]::IsNullOrWhiteSpace($managementKey)) {
        Write-Host '[SKIP] No Management Key provided.'
    }
    else {
        $analyticsResponse = Invoke-OpenRouterGet -Url 'https://openrouter.ai/api/v1/analytics/meta' -ApiKey $managementKey
        Write-Host ('HTTP {0} {1}' -f $analyticsResponse.StatusCode, $analyticsResponse.Reason)

        if ($analyticsResponse.StatusCode -ge 200 -and $analyticsResponse.StatusCode -lt 300) {
            $analyticsJson = Read-JsonSafe $analyticsResponse.Body
            Write-Host '[PASS] Analytics API is accessible with the Management Key.'

            if ($null -ne $analyticsJson -and $null -ne $analyticsJson.data) {
                Write-AnalyticsMetaList 'metrics' $analyticsJson.data.metrics
                Write-AnalyticsMetaList 'dimensions' $analyticsJson.data.dimensions
                Write-AnalyticsMetaList 'operators' $analyticsJson.data.operators
                Write-AnalyticsMetaList 'granularities' $analyticsJson.data.granularities
            }
            else {
                Write-Host '[WARN] Analytics metadata response has no data object.'
            }
        }
        elseif ($analyticsResponse.StatusCode -eq 401 -or $analyticsResponse.StatusCode -eq 403) {
            Write-Host '[FAIL] Analytics API rejected the Management Key.'
        }
        else {
            Write-Host '[WARN] Analytics API returned an unexpected status.'
        }
    }

    Write-Host ''
    Write-Host 'PROBE COMPLETE'
    Write-Host 'Copy this output back for analysis. Do not paste either key.'
}
finally {
    $apiKey = $null
    $managementKey = $null
    $secureApiKey = $null
    $secureManagementKey = $null
}
