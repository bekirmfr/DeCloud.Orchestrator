# Decentralized Cloud Orchestrator

The central brain that coordinates nodes, schedules VMs, and provides the API for the frontend.

## Quick Start

### 1. Run the Orchestrator

```bash
cd src/Orchestrator
dotnet restore
dotnet run
```

The API will be available at:
- **Swagger UI**: http://localhost:5000/swagger
- **Health Check**: http://localhost:5000/health
- **SignalR Hub**: ws://localhost:5000/hub/orchestrator

### 2. Test Basic Endpoints

```bash
# Health check
curl http://localhost:5000/health

# Get system stats
curl http://localhost:5000/api/system/stats

# Get available images
curl http://localhost:5000/api/system/images

# Get pricing tiers
curl http://localhost:5000/api/system/pricing
```

## Authentication Flow

### Step 1: Get a message to sign
```bash
curl "http://localhost:5000/api/auth/message?walletAddress=0x1234567890abcdef"
```

### Step 2: Authenticate with wallet signature
```bash
curl -X POST http://localhost:5000/api/auth/wallet \
  -H "Content-Type: application/json" \
  -d '{
    "walletAddress": "0x1234567890abcdef1234567890abcdef12345678",
    "signature": "0xmocksignature",
    "message": "Sign this message...",
    "timestamp": '$(date +%s)'
  }'
```

### Step 3: Use the token
```bash
export TOKEN="eyJhbG..."
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/user/me
```

## Node Agent Flow

### 1. Register a Node

```bash
curl -X POST http://localhost:5000/api/nodes/register \
  -H "Content-Type: application/json" \
  -d '{
    "name": "node-1",
    "walletAddress": "0xNodeWallet1234567890abcdef1234567890abcd",
    "publicIp": "192.168.1.100",
    "agentPort": 5050,
    "resources": {
      "cpuCores": 8,
      "memoryMb": 16384,
      "storageGb": 500,
      "bandwidthMbps": 1000
    },
    "agentVersion": "1.0.0",
    "supportedImages": ["ubuntu-24.04", "ubuntu-22.04", "debian-12"],
    "supportsGpu": false,
    "gpuInfo": null,
    "region": "us-east",
    "zone": "us-east-1a"
  }'
```

**Save the `nodeId` and `authToken` from the response!**

### 2. Send Heartbeats (every 30 seconds)

```bash
curl -X POST "http://localhost:5000/api/nodes/$NODE_ID/heartbeat" \
  -H "Content-Type: application/json" \
  -H "X-Node-Token: $NODE_TOKEN" \
  -d '{
    "nodeId": "'$NODE_ID'",
    "metrics": {
      "cpuUsagePercent": 25.5,
      "memoryUsagePercent": 45.0,
      "storageUsagePercent": 30.0,
      "networkInMbps": 100,
      "networkOutMbps": 50,
      "activeVmCount": 0,
      "loadAverage": 1.5
    },
    "availableResources": {
      "cpuCores": 6,
      "memoryMb": 12000,
      "storageGb": 400,
      "bandwidthMbps": 1000
    },
    "activeVmIds": []
  }'
```

## VM Lifecycle

### Create a VM
```bash
curl -X POST http://localhost:5000/api/vms \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "my-ubuntu-vm",
    "spec": {
      "cpuCores": 2,
      "memoryMb": 4096,
      "diskGb": 40,
      "imageId": "ubuntu-24.04",
      "requiresGpu": false
    }
  }'
```

### List / Get / Delete VMs
```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/vms
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/vms/{vmId}
curl -X DELETE -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/vms/{vmId}
```

### VM Actions (Start/Stop/Restart)
```bash
curl -X POST http://localhost:5000/api/vms/{vmId}/action \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"action": "Stop"}'
```

## Testing Tools

See the `tools/` directory for:
- `mock-node-agent.sh` - Bash script to simulate a node agent
- `test-flow.sh` - End-to-end test script

## Docker

```bash
docker build -t orchestrator .
docker run -p 5000:5000 orchestrator
```
