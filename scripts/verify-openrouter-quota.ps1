param()

$ErrorActionPreference = 'Stop'

Write-Host 'QuotaTray OpenRouter quota probe'
Write-Host 'Queries current OpenRouter endpoints and prints only quota-relevant fields.'
Write-Host 'The API key itself is never printed.'
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

$apiKey = $env:OPENROUTER_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $secureKey = Read-Host 'OpenRouter API key' -AsSecureString
    $apiKey = Get-PlainTextFromSecureString -Secure $secureKey
}

if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Error 'No OpenRouter API key provided.'
    exit 2
}

try {
    Write-Host '[1/3] GET /api/v1/key'
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
        Write-Host '[WARN] /key is not available for this key.'
    }

    Write-Host ''
    Write-Host '[2/3] GET /api/v1/credits'
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
        Write-Host '[INFO] /credits is unavailable for this key; provider should treat it as optional.'
    }

    Write-Host ''
    Write-Host '[3/3] GET /api/v1/analytics/meta'
    $analyticsResponse = Invoke-OpenRouterGet -Url 'https://openrouter.ai/api/v1/analytics/meta' -ApiKey $apiKey
    Write-Host ('HTTP {0} {1}' -f $analyticsResponse.StatusCode, $analyticsResponse.Reason)

    if ($analyticsResponse.StatusCode -ge 200 -and $analyticsResponse.StatusCode -lt 300) {
        $analyticsJson = Read-JsonSafe $analyticsResponse.Body
        Write-Host '[PASS] Analytics API is accessible with this key.'

        if ($null -ne $analyticsJson) {
            $propertyNames = @($analyticsJson.PSObject.Properties.Name)
            if ($propertyNames.Count -gt 0) {
                Write-Host ('- top-level fields: {0}' -f ($propertyNames -join ', '))
            }
        }
    }
    elseif ($analyticsResponse.StatusCode -eq 401 -or $analyticsResponse.StatusCode -eq 403) {
        Write-Host '[INFO] Analytics API is not accessible with this regular API key.'
        Write-Host '       OpenRouter documents Analytics API access as requiring a Management Key.'
    }
    else {
        Write-Host '[WARN] Analytics API returned an unexpected status; keep free-request usage optional.'
    }

    Write-Host ''
    Write-Host 'PROBE COMPLETE'
    Write-Host 'Copy this output back for analysis. Do not paste your API key.'
}
finally {
    $apiKey = $null
}
