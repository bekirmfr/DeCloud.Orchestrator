using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Renders complete DHT VM cloud-init YAML with the Go binary embedded.
/// The Orchestrator owns the full cloud-init lifecycle — no NodeAgent templates needed.
/// The rendered YAML is sent as CustomCloudInit in the CreateVmRequest, so the
/// NodeAgent simply writes it to disk and boots the VM.
/// </summary>
public interface IDhtCloudInitProvider
{
    /// <summary>
    /// Render a fully-substituted cloud-init YAML for a DHT VM.
    /// Includes the base64-encoded DHT binary, systemd units, health check,
    /// and ready callback — everything the VM needs to boot and join the DHT network.
    /// </summary>
    Task<string> RenderCloudInitAsync(DhtCloudInitParams parameters, CancellationToken ct = default);
}

/// <summary>
/// Parameters for rendering a DHT cloud-init template.
/// Collected by DhtNodeService before calling RenderCloudInitAsync.
/// </summary>
public record DhtCloudInitParams
{
    public required string VmId { get; init; }
    public required string VmName { get; init; }
    public required string NodeId { get; init; }
    public required string Region { get; init; }
    public required string AdvertiseIp { get; init; }
    public required string BootstrapPeers { get; init; }
    public required string Architecture { get; init; }
    public int ListenPort { get; init; } = 4001;
    public int ApiPort { get; init; } = 5080;
}

public class DhtCloudInitProvider : IDhtCloudInitProvider
{
    private readonly ILogger<DhtCloudInitProvider> _logger;
    private readonly string _binaryBasePath;

    // Cache loaded binaries in memory (they don't change at runtime)
    private readonly Dictionary<string, string> _binaryCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public DhtCloudInitProvider(ILogger<DhtCloudInitProvider> logger, IConfiguration configuration)
    {
        _logger = logger;

        // Binary path: configurable, defaults to dht-node/ relative to app base
        _binaryBasePath = configuration["DhtNode:BinaryPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "dht-node");
    }

    public async Task<string> RenderCloudInitAsync(DhtCloudInitParams p, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Rendering DHT cloud-init for VM {VmId} (arch={Arch}, region={Region})",
            p.VmId, p.Architecture, p.Region);

        // Load the pre-built binary for the target architecture
        var binaryBase64 = await LoadBinaryAsync(p.Architecture, ct);

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var hostname = SanitizeHostname(p.VmName);

        // Render the complete cloud-init YAML
        var yaml = CloudInitTemplate
            .Replace("{{HOSTNAME}}", hostname)
            .Replace("{{VM_ID}}", p.VmId)
            .Replace("{{VM_NAME}}", p.VmName)
            .Replace("{{NODE_ID}}", p.NodeId)
            .Replace("{{REGION}}", p.Region)
            .Replace("{{LISTEN_PORT}}", p.ListenPort.ToString())
            .Replace("{{API_PORT}}", p.ApiPort.ToString())
            .Replace("{{ADVERTISE_IP}}", p.AdvertiseIp)
            .Replace("{{BOOTSTRAP_PEERS}}", p.BootstrapPeers)
            .Replace("{{TIMESTAMP}}", timestamp)
            .Replace("{{DHT_NODE_BINARY_BASE64}}", binaryBase64);

        _logger.LogInformation(
            "DHT cloud-init rendered for VM {VmId}: {Size}KB",
            p.VmId, yaml.Length / 1024);

        return yaml;
    }

    private async Task<string> LoadBinaryAsync(string architecture, CancellationToken ct)
    {
        // Normalize architecture name
        var arch = architecture switch
        {
            "x86_64" or "amd64" => "amd64",
            "aarch64" or "arm64" => "arm64",
            _ => throw new ArgumentException($"Unsupported architecture: {architecture}")
        };

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_binaryCache.TryGetValue(arch, out var cached))
                return cached;

            var fileName = $"dht-node-{arch}.b64";
            var filePath = Path.Combine(_binaryBasePath, fileName);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"DHT binary not found at {filePath}. " +
                    "Build with: cd dht-node && bash build.sh " +
                    "(or ensure Docker build completed successfully).",
                    filePath);
            }

            var base64 = (await File.ReadAllTextAsync(filePath, ct)).Trim();

            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException($"DHT binary file is empty: {filePath}");

            _binaryCache[arch] = base64;

            _logger.LogInformation(
                "Loaded DHT binary: {Path} ({SizeKB}KB base64)",
                filePath, base64.Length / 1024);

            return base64;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static string SanitizeHostname(string name)
    {
        return new string(name.ToLower()
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(63)
            .ToArray());
    }

    // =========================================================================
    // Complete DHT VM Cloud-Init Template
    // =========================================================================
    // All scripts are inlined — no external template files, no NodeAgent dependencies.
    // The Orchestrator renders this fully and sends it as CustomCloudInit.
    // =========================================================================

    private const string CloudInitTemplate = @"#cloud-config
# DeCloud DHT VM Cloud-Init Configuration
# Runs a libp2p Kademlia DHT node for peer discovery,
# key-value storage, and GossipSub event propagation.
# Version: 3.0 (No Docker - direct binary via systemd)
# Rendered by Orchestrator DhtCloudInitProvider

hostname: {{HOSTNAME}}
manage_etc_hosts: true

# Regenerate machine-id to prevent conflicts
bootcmd:
  - [ sh, -c, 'rm -f /etc/machine-id && systemd-machine-id-setup' ]

# Security hardening
disable_root: true

users:
  - name: dht
    groups: [sudo]
    sudo: ['ALL=(ALL) NOPASSWD:ALL']
    shell: /bin/bash
    lock_passwd: true
    ssh_authorized_keys: []

ssh_pwauth: false

# =====================================================
# SYSTEM PACKAGES
# =====================================================
packages:
  - qemu-guest-agent
  - curl
  - jq
  - net-tools
  - openssl

# =====================================================
# SYSTEM CONFIGURATION
# =====================================================
write_files:
  # DHT binary (base64-encoded Go binary, decoded at boot)
  - path: /opt/decloud-dht/dht-node.b64
    permissions: '0644'
    content: |
      {{DHT_NODE_BINARY_BASE64}}

  # DHT environment file (read by systemd EnvironmentFile)
  - path: /etc/decloud-dht/dht.env
    permissions: '0644'
    content: |
      DHT_LISTEN_PORT={{LISTEN_PORT}}
      DHT_API_PORT={{API_PORT}}
      DHT_ADVERTISE_IP={{ADVERTISE_IP}}
      DHT_BOOTSTRAP_PEERS={{BOOTSTRAP_PEERS}}
      DHT_DATA_DIR=/var/lib/decloud-dht
      DECLOUD_NODE_ID={{NODE_ID}}
      DECLOUD_REGION={{REGION}}

  # DHT metadata
  - path: /etc/decloud/dht-metadata.json
    permissions: '0644'
    content: |
      {
        ""node_id"": ""{{NODE_ID}}"",
        ""vm_id"": ""{{VM_ID}}"",
        ""vm_name"": ""{{VM_NAME}}"",
        ""region"": ""{{REGION}}"",
        ""listen_port"": {{LISTEN_PORT}},
        ""api_port"": {{API_PORT}},
        ""advertise_ip"": ""{{ADVERTISE_IP}}"",
        ""created_at"": ""{{TIMESTAMP}}"",
        ""version"": ""3.0""
      }

  # DHT systemd service (runs binary directly, no Docker)
  - path: /etc/systemd/system/decloud-dht.service
    permissions: '0644'
    content: |
      [Unit]
      Description=DeCloud DHT Node (libp2p Kademlia)
      After=network-online.target
      Wants=network-online.target

      [Service]
      Type=simple
      User=dht
      EnvironmentFile=/etc/decloud-dht/dht.env
      ExecStart=/usr/local/bin/dht-node
      Restart=always
      RestartSec=5

      # Hardening
      ProtectSystem=strict
      ReadWritePaths=/var/lib/decloud-dht
      ProtectHome=true
      NoNewPrivileges=true

      [Install]
      WantedBy=multi-user.target

  # DHT ready callback service
  - path: /etc/systemd/system/decloud-dht-callback.service
    permissions: '0644'
    content: |
      [Unit]
      Description=DeCloud DHT Ready Callback
      After=decloud-dht.service network-online.target
      Wants=decloud-dht.service network-online.target

      [Service]
      Type=oneshot
      ExecStartPre=/bin/bash -c 'if [ -f /var/lib/decloud-dht/callback-complete ]; then exit 0; fi'
      ExecStart=/usr/local/bin/dht-notify-ready.sh
      RemainAfterExit=yes
      StandardOutput=journal
      StandardError=journal
      Restart=on-failure
      RestartSec=10s
      StartLimitBurst=5
      TimeoutStartSec=180s

      [Install]
      WantedBy=multi-user.target

  # Health check script
  - path: /usr/local/bin/dht-health-check.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      # DeCloud DHT Node Health Check
      # Exit 0 = healthy, Exit 1 = unhealthy
      API_PORT=""{{API_PORT}}""
      API_URL=""http://127.0.0.1:${API_PORT}/health""
      RESPONSE=$(curl -s --max-time 5 ""$API_URL"" 2>/dev/null)
      CURL_EXIT=$?
      if [ $CURL_EXIT -ne 0 ]; then
          echo '{""status"":""unreachable"",""error"":""curl failed""}'
          exit 1
      fi
      echo ""$RESPONSE""
      STATUS=$(echo ""$RESPONSE"" | jq -r '.status' 2>/dev/null)
      if [ ""$STATUS"" = ""active"" ]; then
          exit 0
      else
          exit 1
      fi

  # Ready callback script
  - path: /usr/local/bin/dht-notify-ready.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      # DeCloud DHT Node Ready Callback
      set -e
      LOG_FILE=""/var/log/decloud-dht-callback.log""
      MARKER_FILE=""/var/lib/decloud-dht/callback-complete""
      log() { echo ""[$(date +'%Y-%m-%d %H:%M:%S')] $*"" | tee -a ""$LOG_FILE""; }
      if [ -f ""$MARKER_FILE"" ]; then
          log ""DHT callback already completed - skipping""
          exit 0
      fi
      log ""Starting DHT ready callback...""
      API_PORT=""{{API_PORT}}""
      VM_ID=""{{VM_ID}}""
      # Detect gateway IP (node agent host)
      GATEWAY_IP=$(ip route | grep default | awk '{print $3}' | head -1)
      if [ -z ""$GATEWAY_IP"" ]; then GATEWAY_IP=""192.168.122.1""; fi
      NODE_AGENT_URL=""http://${GATEWAY_IP}:5100""
      log ""Node agent URL: $NODE_AGENT_URL""
      # Wait for DHT binary to start and obtain peer ID
      log ""Waiting for DHT node to start...""
      PEER_ID=""""
      for i in $(seq 1 60); do
          HEALTH=$(curl -s --max-time 3 ""http://127.0.0.1:${API_PORT}/health"" 2>/dev/null)
          if [ $? -eq 0 ]; then
              PEER_ID=$(echo ""$HEALTH"" | jq -r '.peerId' 2>/dev/null)
              CONNECTED=$(echo ""$HEALTH"" | jq -r '.connectedPeers' 2>/dev/null)
              if [ -n ""$PEER_ID"" ] && [ ""$PEER_ID"" != ""null"" ]; then
                  log ""DHT node started - peer ID: $PEER_ID (connected peers: $CONNECTED)""
                  echo ""$PEER_ID"" > /var/lib/decloud-dht/peer-id
                  break
              fi
          fi
          if [ $((i % 10)) -eq 0 ]; then log ""  Still waiting for DHT node... (attempt $i/60)""; fi
          sleep 2
      done
      if [ -z ""$PEER_ID"" ] || [ ""$PEER_ID"" = ""null"" ]; then
          log ""Failed to get peer ID after 120 seconds""
          exit 1
      fi
      # Verify node agent is reachable
      log ""Checking if node agent is reachable...""
      for i in $(seq 1 12); do
          if curl -s -m 2 ""$NODE_AGENT_URL/health"" >/dev/null 2>&1; then
              log ""Node agent is reachable""
              break
          fi
          if [ $i -eq 12 ]; then log ""Node agent not reachable after 60 seconds""; exit 1; fi
          sleep 5
      done
      # Compute authentication token
      MACHINE_ID=$(cat /etc/machine-id 2>/dev/null || echo ""unknown"")
      MESSAGE=""${VM_ID}:${PEER_ID}""
      TOKEN=$(echo -n ""$MESSAGE"" | openssl dgst -sha256 -hmac ""$MACHINE_ID"" -binary | base64)
      # Notify node agent with our peer ID
      log ""Notifying node agent of DHT peer ID...""
      RESPONSE=$(curl -X POST ""$NODE_AGENT_URL/api/dht/ready"" \
          -H ""Content-Type: application/json"" \
          -H ""X-DHT-Token: $TOKEN"" \
          -d ""{
              \""vmId\"": \""$VM_ID\"",
              \""peerId\"": \""$PEER_ID\""
          }"" \
          --max-time 10 \
          --retry 2 \
          --retry-delay 5 \
          -w ""\nHTTP_CODE:%{http_code}"" \
          -s \
          2>&1)
      HTTP_CODE=$(echo ""$RESPONSE"" | grep -oP 'HTTP_CODE:\K\d+')
      if [ ""$HTTP_CODE"" = ""200"" ]; then
          log ""Successfully notified node agent - DHT node is active""
          mkdir -p ""$(dirname ""$MARKER_FILE"")""
          echo ""DHT callback completed at $(date) - peer ID: $PEER_ID"" > ""$MARKER_FILE""
          logger -t decloud-dht ""DHT node active with peer ID $PEER_ID""
          exit 0
      else
          log ""Failed to notify node agent (HTTP ${HTTP_CODE:-timeout})""
          log ""Response: $RESPONSE""
          exit 1
      fi

# =====================================================
# RUNTIME COMMANDS
# =====================================================
runcmd:
  # Enable and start QEMU guest agent
  - systemctl enable qemu-guest-agent
  - systemctl start qemu-guest-agent

  # Create directories
  - mkdir -p /etc/decloud-dht
  - mkdir -p /etc/decloud
  - mkdir -p /var/lib/decloud-dht
  - mkdir -p /opt/decloud-dht
  - chown -R dht:dht /etc/decloud-dht
  - chown -R dht:dht /var/lib/decloud-dht
  - chown -R dht:dht /opt/decloud-dht

  # Decode and install the DHT binary
  - base64 -d /opt/decloud-dht/dht-node.b64 > /usr/local/bin/dht-node
  - chmod 755 /usr/local/bin/dht-node
  - rm -f /opt/decloud-dht/dht-node.b64

  # Start DHT service
  - systemctl daemon-reload
  - systemctl enable decloud-dht
  - systemctl start decloud-dht

  # Start ready callback service
  - systemctl enable decloud-dht-callback
  - systemctl start decloud-dht-callback

  # Log completion
  - 'echo ""DeCloud DHT node {{VM_ID}} bootstrap complete at $(date)"" >> /var/log/dht-bootstrap.log'
  - 'echo ""Region: {{REGION}}"" >> /var/log/dht-bootstrap.log'
  - 'echo ""Advertise IP: {{ADVERTISE_IP}}"" >> /var/log/dht-bootstrap.log'

final_message: ""DeCloud DHT Node {{VM_NAME}} is ready!""
";
}
