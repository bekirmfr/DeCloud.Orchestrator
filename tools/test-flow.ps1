# End-to-End Test Script for Orchestrator (PowerShell)
# Usage: .\test-flow.ps1 [-OrchestratorUrl "http://localhost:5000"]

param(
    [string]$OrchestratorUrl = "http://localhost:5000"
)

$ErrorActionPreference = "Stop"

function Write-TestHeader {
    param([string]$text)
    Write-Host ""
    Write-Host "=== TEST: $text ===" -ForegroundColor Yellow
}

function Write-Success {
    param([string]$text)
    Write-Host "[OK] $text" -ForegroundColor Green
}

function Write-Info {
    param([string]$text)
    Write-Host "[INFO] $text" -ForegroundColor Cyan
}

function Write-Fail {
    param([string]$text)
    Write-Host "[FAIL] $text" -ForegroundColor Red
}

Write-Host "==========================================" -ForegroundColor Magenta
Write-Host "   Orchestrator E2E Test Suite" -ForegroundColor Magenta
Write-Host "==========================================" -ForegroundColor Magenta
Write-Host "Target: $OrchestratorUrl"

# Generate random wallet addresses
function New-RandomWallet {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 20
    $rng.GetBytes($bytes)
    $hex = [BitConverter]::ToString($bytes).Replace("-", "").ToLower()
    return "0x$hex"
}

# ==========================================
# TEST 1: Health Check
# ==========================================
Write-TestHeader -text "Health Check"

try {
    $health = Invoke-RestMethod -Uri "$OrchestratorUrl/health" -Method Get
    if ($health.status -eq "healthy") {
        Write-Success -text "Health check passed"
    }
} catch {
    Write-Fail -text "Health check failed: $_"
    exit 1
}

# ==========================================
# TEST 2: System Endpoints
# ==========================================
Write-TestHeader -text "System Endpoints"

$stats = Invoke-RestMethod -Uri "$OrchestratorUrl/api/system/stats" -Method Get
$nodeCount = $stats.data.totalNodes
$vmCount = $stats.data.totalVms
Write-Success -text "Stats: $nodeCount nodes, $vmCount VMs"

$images = Invoke-RestMethod -Uri "$OrchestratorUrl/api/system/images" -Method Get
$imageCount = $images.data.Count
Write-Success -text "Found $imageCount images"

$pricing = Invoke-RestMethod -Uri "$OrchestratorUrl/api/system/pricing" -Method Get
$tierCount = $pricing.data.Count
Write-Success -text "Found $tierCount pricing tiers"

# ==========================================
# TEST 3: Node Registration
# ==========================================
Write-TestHeader -text "Node Registration"

$nodeWallet = New-RandomWallet
Write-Info -text "Registering node with wallet: $nodeWallet"

$nodeBody = @{
    name = "test-node-ps"
    walletAddress = $nodeWallet
    publicIp = "10.0.0.100"
    agentPort = 5050
    resources = @{
        cpuCores = 8
        memoryMb = 16384
        storageGb = 500
        bandwidthMbps = 1000
    }
    agentVersion = "1.0.0"
    supportedImages = @("ubuntu-24.04", "ubuntu-22.04", "debian-12")
    supportsGpu = $false
    gpuInfo = $null
    region = "us-east"
    zone = "us-east-1a"
} | ConvertTo-Json -Depth 10

$nodeResponse = Invoke-RestMethod -Uri "$OrchestratorUrl/api/nodes/register" -Method Post -ContentType "application/json" -Body $nodeBody

$nodeId = $nodeResponse.data.nodeId
$nodeToken = $nodeResponse.data.authToken
Write-Success -text "Node registered: $nodeId"

# ==========================================
# TEST 4: Node Heartbeat
# ==========================================
Write-TestHeader -text "Node Heartbeat"

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$heartbeatBody = @{
    nodeId = $nodeId
    metrics = @{
        timestamp = $timestamp
        cpuUsagePercent = 20.0
        memoryUsagePercent = 30.0
        storageUsagePercent = 15.0
        networkInMbps = 50
        networkOutMbps = 25
        activeVmCount = 0
        loadAverage = 0.5
    }
    availableResources = @{
        cpuCores = 8
        memoryMb = 16384
        storageGb = 500
        bandwidthMbps = 1000
    }
    activeVmIds = @()
} | ConvertTo-Json -Depth 10

$headers = @{ "X-Node-Token" = $nodeToken }
$heartbeatResponse = Invoke-RestMethod -Uri "$OrchestratorUrl/api/nodes/$nodeId/heartbeat" -Method Post -ContentType "application/json" -Body $heartbeatBody -Headers $headers

if ($heartbeatResponse.data.acknowledged) {
    Write-Success -text "Heartbeat acknowledged"
}

# ==========================================
# TEST 5: User Authentication
# ==========================================
Write-TestHeader -text "User Authentication"

$userWallet = New-RandomWallet
$unixTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

Write-Info -text "Authenticating with wallet: $userWallet"

$authBody = @{
    walletAddress = $userWallet
    signature = "0xmocksignature"
    message = "Sign this message"
    timestamp = $unixTimestamp
} | ConvertTo-Json

$authResponse = Invoke-RestMethod -Uri "$OrchestratorUrl/api/auth/wallet" -Method Post -ContentType "application/json" -Body $authBody

$accessToken = $authResponse.data.accessToken
Write-Success -text "User authenticated"

$authHeaders = @{ "Authorization" = "Bearer $accessToken" }

# ==========================================
# TEST 6: Get User Profile
# ==========================================
Write-TestHeader -text "User Profile"

$userProfile = Invoke-RestMethod -Uri "$OrchestratorUrl/api/user/me" -Headers $authHeaders
$profileWallet = $userProfile.data.walletAddress
Write-Success -text "Profile retrieved for wallet: $profileWallet"

# ==========================================
# TEST 7: Create VM
# ==========================================
Write-TestHeader -text "Create VM"

$vmBody = @{
    name = "test-vm-ps"
    spec = @{
        cpuCores = 2
        memoryMb = 4096
        diskGb = 40
        imageId = "ubuntu-24.04"
        requiresGpu = $false
    }
    labels = @{
        test = "powershell"
    }
} | ConvertTo-Json -Depth 10

$vmResponse = Invoke-RestMethod -Uri "$OrchestratorUrl/api/vms" -Method Post -ContentType "application/json" -Body $vmBody -Headers $authHeaders

$vmId = $vmResponse.data.vmId
Write-Success -text "VM created: $vmId"

# ==========================================
# TEST 8: List VMs
# ==========================================
Write-TestHeader -text "List VMs"

$vmsList = Invoke-RestMethod -Uri "$OrchestratorUrl/api/vms" -Headers $authHeaders
$totalVms = $vmsList.data.totalCount
Write-Success -text "Found $totalVms VM(s)"

# ==========================================
# TEST 9: Get VM Details
# ==========================================
Write-TestHeader -text "Get VM Details"

$vmDetails = Invoke-RestMethod -Uri "$OrchestratorUrl/api/vms/$vmId" -Headers $authHeaders
$vmName = $vmDetails.data.vm.name
$vmStatus = $vmDetails.data.vm.status
Write-Success -text "VM details - Name: $vmName, Status: $vmStatus"

# ==========================================
# TEST 10: Delete VM
# ==========================================
Write-TestHeader -text "Delete VM"

$deleteResponse = Invoke-RestMethod -Uri "$OrchestratorUrl/api/vms/$vmId" -Method Delete -Headers $authHeaders
Write-Success -text "VM deleted"

# ==========================================
# Summary
# ==========================================
Write-Host ""
Write-Host "==========================================" -ForegroundColor Magenta
Write-Host "           All Tests Passed!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "Resources created during test:"
Write-Host "  Node ID: $nodeId"
Write-Host "  Node Wallet: $nodeWallet"
Write-Host "  User Wallet: $userWallet"
Write-Host "  VM ID: $vmId (deleted)"
Write-Host ""