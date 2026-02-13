using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Renders complete cloud-init YAML for ALL system VM types.
/// The Orchestrator owns the full cloud-init lifecycle — no NodeAgent templates needed.
/// The rendered YAML is sent as CustomCloudInit in CreateVmRequest, so the NodeAgent
/// simply writes it to disk and boots the VM.
///
/// Supported roles: Dht, Relay, BlockStore, Ingress
/// </summary>
public interface ISystemVmCloudInitProvider
{
    Task<string> RenderDhtCloudInitAsync(DhtCloudInitParams parameters, CancellationToken ct = default);
    Task<string> RenderRelayCloudInitAsync(RelayCloudInitParams parameters, CancellationToken ct = default);
    Task<string> RenderBlockStoreCloudInitAsync(BlockStoreCloudInitParams parameters, CancellationToken ct = default);
    Task<string> RenderIngressCloudInitAsync(IngressCloudInitParams parameters, CancellationToken ct = default);
}

// =========================================================================
// Per-role parameter records
// =========================================================================

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

public record RelayCloudInitParams
{
    public required string VmId { get; init; }
    public required string VmName { get; init; }
    public required string NodeId { get; init; }
    public required string Region { get; init; }
    public required string NodePublicIp { get; init; }
    public required string WireGuardPrivateKey { get; init; }
    public required int RelaySubnet { get; init; }
    public int WireGuardPort { get; init; } = 51820;
    public int MaxConnections { get; init; } = 40;
    public string OrchestratorUrl { get; init; } = "";
}

public record BlockStoreCloudInitParams
{
    public required string VmId { get; init; }
    public required string VmName { get; init; }
    public required string NodeId { get; init; }
    public required string Region { get; init; }
    public required string Architecture { get; init; }
}

public record IngressCloudInitParams
{
    public required string VmId { get; init; }
    public required string VmName { get; init; }
    public required string NodeId { get; init; }
    public required string Region { get; init; }
    public required string NodePublicIp { get; init; }
}

// =========================================================================
// Implementation
// =========================================================================

public class SystemVmCloudInitProvider : ISystemVmCloudInitProvider
{
    private readonly ILogger<SystemVmCloudInitProvider> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _binaryBasePath;

    // Cache loaded binaries in memory (they don't change at runtime)
    private readonly Dictionary<string, string> _binaryCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public SystemVmCloudInitProvider(
        ILogger<SystemVmCloudInitProvider> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        _binaryBasePath = configuration["DhtNode:BinaryPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "dht-node");
    }

    // =========================================================================
    // DHT
    // =========================================================================

    public async Task<string> RenderDhtCloudInitAsync(DhtCloudInitParams p, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Rendering DHT cloud-init for VM {VmId} (arch={Arch}, region={Region})",
            p.VmId, p.Architecture, p.Region);

        var binaryBase64 = await LoadDhtBinaryAsync(p.Architecture, ct);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var yaml = DhtTemplate
            .Replace("{{HOSTNAME}}", SanitizeHostname(p.VmName))
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

        _logger.LogInformation("DHT cloud-init rendered: {Size}KB", yaml.Length / 1024);
        return yaml;
    }

    // =========================================================================
    // Relay
    // =========================================================================

    public Task<string> RenderRelayCloudInitAsync(RelayCloudInitParams p, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Rendering Relay cloud-init for VM {VmId} (subnet={Subnet}, region={Region})",
            p.VmId, p.RelaySubnet, p.Region);

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var tunnelIp = $"10.20.{p.RelaySubnet}.254";

        var yaml = RelayTemplate
            .Replace("{{HOSTNAME}}", SanitizeHostname(p.VmName))
            .Replace("{{VM_ID}}", p.VmId)
            .Replace("{{VM_NAME}}", p.VmName)
            .Replace("{{NODE_ID}}", p.NodeId)
            .Replace("{{REGION}}", p.Region)
            .Replace("{{NODE_PUBLIC_IP}}", p.NodePublicIp)
            .Replace("{{WIREGUARD_PRIVATE_KEY}}", p.WireGuardPrivateKey)
            .Replace("{{WIREGUARD_PORT}}", p.WireGuardPort.ToString())
            .Replace("{{RELAY_SUBNET}}", p.RelaySubnet.ToString())
            .Replace("{{TUNNEL_IP}}", tunnelIp)
            .Replace("{{MAX_CONNECTIONS}}", p.MaxConnections.ToString())
            .Replace("{{ORCHESTRATOR_URL}}", p.OrchestratorUrl)
            .Replace("{{TIMESTAMP}}", timestamp);

        _logger.LogInformation("Relay cloud-init rendered: {Size}KB", yaml.Length / 1024);
        return Task.FromResult(yaml);
    }

    // =========================================================================
    // BlockStore (stub — template ready for implementation)
    // =========================================================================

    public Task<string> RenderBlockStoreCloudInitAsync(BlockStoreCloudInitParams p, CancellationToken ct = default)
    {
        _logger.LogInformation("Rendering BlockStore cloud-init for VM {VmId}", p.VmId);

        var yaml = BlockStoreTemplate
            .Replace("{{HOSTNAME}}", SanitizeHostname(p.VmName))
            .Replace("{{VM_ID}}", p.VmId)
            .Replace("{{VM_NAME}}", p.VmName)
            .Replace("{{NODE_ID}}", p.NodeId)
            .Replace("{{REGION}}", p.Region)
            .Replace("{{TIMESTAMP}}", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return Task.FromResult(yaml);
    }

    // =========================================================================
    // Ingress (stub — template ready for implementation)
    // =========================================================================

    public Task<string> RenderIngressCloudInitAsync(IngressCloudInitParams p, CancellationToken ct = default)
    {
        _logger.LogInformation("Rendering Ingress cloud-init for VM {VmId}", p.VmId);

        var yaml = IngressTemplate
            .Replace("{{HOSTNAME}}", SanitizeHostname(p.VmName))
            .Replace("{{VM_ID}}", p.VmId)
            .Replace("{{VM_NAME}}", p.VmName)
            .Replace("{{NODE_ID}}", p.NodeId)
            .Replace("{{REGION}}", p.Region)
            .Replace("{{NODE_PUBLIC_IP}}", p.NodePublicIp)
            .Replace("{{TIMESTAMP}}", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return Task.FromResult(yaml);
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    private async Task<string> LoadDhtBinaryAsync(string architecture, CancellationToken ct)
    {
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

            var filePath = Path.Combine(_binaryBasePath, $"dht-node-{arch}.b64");

            if (!File.Exists(filePath))
                throw new FileNotFoundException(
                    $"DHT binary not found at {filePath}. " +
                    "Build with: cd dht-node && bash build.sh",
                    filePath);

            var base64 = (await File.ReadAllTextAsync(filePath, ct)).Trim();
            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException($"DHT binary file is empty: {filePath}");

            _binaryCache[arch] = base64;
            _logger.LogInformation("Loaded DHT binary: {Path} ({SizeKB}KB)", filePath, base64.Length / 1024);
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
    //  TEMPLATES
    // =========================================================================
    // Each system VM role has a complete, self-contained cloud-init template.
    // All scripts are inlined. No external files. No NodeAgent dependencies.
    // =========================================================================

    #region DHT Template

    private const string DhtTemplate = @"#cloud-config
# DeCloud DHT VM Cloud-Init — libp2p Kademlia node
# Rendered by Orchestrator SystemVmCloudInitProvider

hostname: {{HOSTNAME}}
manage_etc_hosts: true

bootcmd:
  - [ sh, -c, 'rm -f /etc/machine-id && systemd-machine-id-setup' ]

disable_root: true

users:
  - name: dht
    groups: [sudo]
    sudo: ['ALL=(ALL) NOPASSWD:ALL']
    shell: /bin/bash
    lock_passwd: true
    ssh_authorized_keys: []

ssh_pwauth: false

packages:
  - qemu-guest-agent
  - curl
  - jq
  - net-tools
  - openssl

write_files:
  - path: /opt/decloud-dht/dht-node.b64
    permissions: '0644'
    content: |
      {{DHT_NODE_BINARY_BASE64}}

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
      ProtectSystem=strict
      ReadWritePaths=/var/lib/decloud-dht
      ProtectHome=true
      NoNewPrivileges=true
      [Install]
      WantedBy=multi-user.target

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
      Restart=on-failure
      RestartSec=10s
      StartLimitBurst=5
      TimeoutStartSec=180s
      [Install]
      WantedBy=multi-user.target

  - path: /usr/local/bin/dht-health-check.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      API_PORT=""{{API_PORT}}""
      RESPONSE=$(curl -s --max-time 5 ""http://127.0.0.1:${API_PORT}/health"" 2>/dev/null)
      if [ $? -ne 0 ]; then echo '{""status"":""unreachable""}'; exit 1; fi
      echo ""$RESPONSE""
      STATUS=$(echo ""$RESPONSE"" | jq -r '.status' 2>/dev/null)
      [ ""$STATUS"" = ""active"" ] && exit 0 || exit 1

  - path: /usr/local/bin/dht-notify-ready.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      set -e
      LOG=""/var/log/decloud-dht-callback.log""
      MARKER=""/var/lib/decloud-dht/callback-complete""
      log() { echo ""[$(date +'%Y-%m-%d %H:%M:%S')] $*"" | tee -a ""$LOG""; }
      [ -f ""$MARKER"" ] && { log ""Already completed""; exit 0; }
      log ""Starting DHT ready callback...""
      API_PORT=""{{API_PORT}}""; VM_ID=""{{VM_ID}}""
      GW=$(ip route | grep default | awk '{print $3}' | head -1)
      [ -z ""$GW"" ] && GW=""192.168.122.1""
      AGENT=""http://${GW}:5100""
      PEER_ID=""""
      for i in $(seq 1 60); do
        H=$(curl -s --max-time 3 ""http://127.0.0.1:${API_PORT}/health"" 2>/dev/null) && \
        PEER_ID=$(echo ""$H"" | jq -r '.peerId' 2>/dev/null)
        [ -n ""$PEER_ID"" ] && [ ""$PEER_ID"" != ""null"" ] && { log ""Peer ID: $PEER_ID""; break; }
        [ $((i % 10)) -eq 0 ] && log ""  Waiting... ($i/60)""
        sleep 2
      done
      [ -z ""$PEER_ID"" ] || [ ""$PEER_ID"" = ""null"" ] && { log ""Timeout""; exit 1; }
      echo ""$PEER_ID"" > /var/lib/decloud-dht/peer-id
      for i in $(seq 1 12); do
        curl -s -m 2 ""$AGENT/health"" >/dev/null 2>&1 && break
        [ $i -eq 12 ] && { log ""Agent unreachable""; exit 1; }
        sleep 5
      done
      MID=$(cat /etc/machine-id 2>/dev/null || echo unknown)
      TOKEN=$(echo -n ""${VM_ID}:${PEER_ID}"" | openssl dgst -sha256 -hmac ""$MID"" -binary | base64)
      HTTP=$(curl -X POST ""$AGENT/api/dht/ready"" \
        -H ""Content-Type: application/json"" -H ""X-DHT-Token: $TOKEN"" \
        -d ""{\""vmId\"": \""$VM_ID\"", \""peerId\"": \""$PEER_ID\""}"" \
        --max-time 10 --retry 2 --retry-delay 5 -s -o /dev/null -w ""%{http_code}"" 2>&1)
      [ ""$HTTP"" = ""200"" ] && { log ""Success""; echo ""done"" > ""$MARKER""; exit 0; }
      log ""Failed (HTTP $HTTP)""; exit 1

runcmd:
  - systemctl enable qemu-guest-agent && systemctl start qemu-guest-agent
  - mkdir -p /etc/decloud-dht /etc/decloud /var/lib/decloud-dht /opt/decloud-dht
  - chown -R dht:dht /etc/decloud-dht /var/lib/decloud-dht /opt/decloud-dht
  - base64 -d /opt/decloud-dht/dht-node.b64 > /usr/local/bin/dht-node
  - chmod 755 /usr/local/bin/dht-node
  - rm -f /opt/decloud-dht/dht-node.b64
  - systemctl daemon-reload
  - systemctl enable decloud-dht && systemctl start decloud-dht
  - systemctl enable decloud-dht-callback && systemctl start decloud-dht-callback
  - 'echo ""DHT {{VM_ID}} ready at $(date)"" >> /var/log/dht-bootstrap.log'

final_message: ""DeCloud DHT Node {{VM_NAME}} is ready!""
";

    #endregion

    #region Relay Template

    private const string RelayTemplate = @"#cloud-config
# DeCloud Relay VM Cloud-Init — WireGuard relay for CGNAT nodes
# Rendered by Orchestrator SystemVmCloudInitProvider

hostname: {{HOSTNAME}}
manage_etc_hosts: true

bootcmd:
  - [ sh, -c, 'rm -f /etc/machine-id && systemd-machine-id-setup' ]

disable_root: true

users:
  - name: relay
    groups: [sudo]
    sudo: ['ALL=(ALL) NOPASSWD:ALL']
    shell: /bin/bash
    lock_passwd: true
    ssh_authorized_keys: []

ssh_pwauth: false

packages:
  - qemu-guest-agent
  - wireguard
  - wireguard-tools
  - nginx
  - iptables
  - curl
  - jq
  - net-tools

write_files:
  # WireGuard interface configuration
  - path: /etc/wireguard/wg0.conf
    permissions: '0600'
    content: |
      [Interface]
      PrivateKey = {{WIREGUARD_PRIVATE_KEY}}
      Address = {{TUNNEL_IP}}/24
      ListenPort = {{WIREGUARD_PORT}}
      PostUp = iptables -A FORWARD -i wg0 -j ACCEPT; iptables -A FORWARD -o wg0 -j ACCEPT; iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
      PostDown = iptables -D FORWARD -i wg0 -j ACCEPT; iptables -D FORWARD -o wg0 -j ACCEPT; iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE

  # Relay metadata
  - path: /etc/decloud/relay-metadata.json
    permissions: '0644'
    content: |
      {
        ""node_id"": ""{{NODE_ID}}"",
        ""vm_id"": ""{{VM_ID}}"",
        ""vm_name"": ""{{VM_NAME}}"",
        ""region"": ""{{REGION}}"",
        ""node_public_ip"": ""{{NODE_PUBLIC_IP}}"",
        ""wireguard_port"": {{WIREGUARD_PORT}},
        ""relay_subnet"": {{RELAY_SUBNET}},
        ""tunnel_ip"": ""{{TUNNEL_IP}}"",
        ""max_connections"": {{MAX_CONNECTIONS}},
        ""created_at"": ""{{TIMESTAMP}}"",
        ""version"": ""3.0""
      }

  # Nginx health check endpoint
  - path: /etc/nginx/sites-available/relay-health
    permissions: '0644'
    content: |
      server {
          listen 80 default_server;
          server_name _;
          location /health {
              default_type application/json;
              return 200 '{""status"":""active"",""relay_subnet"":{{RELAY_SUBNET}},""tunnel_ip"":""{{TUNNEL_IP}}""}';
          }
      }

  # Relay monitor service
  - path: /etc/systemd/system/decloud-relay-monitor.service
    permissions: '0644'
    content: |
      [Unit]
      Description=DeCloud Relay Monitor
      After=wg-quick@wg0.service network-online.target
      Wants=wg-quick@wg0.service
      [Service]
      Type=simple
      ExecStart=/usr/local/bin/relay-monitor.sh
      Restart=always
      RestartSec=30
      [Install]
      WantedBy=multi-user.target

  # Relay NAT callback service (notifies orchestrator on boot)
  - path: /etc/systemd/system/decloud-relay-nat-callback.service
    permissions: '0644'
    content: |
      [Unit]
      Description=DeCloud Relay NAT Ready Callback
      After=wg-quick@wg0.service network-online.target
      Wants=wg-quick@wg0.service network-online.target
      [Service]
      Type=oneshot
      ExecStartPre=/bin/bash -c 'if [ -f /var/lib/decloud-relay/callback-complete ]; then exit 0; fi'
      ExecStart=/usr/local/bin/relay-notify-ready.sh
      RemainAfterExit=yes
      Restart=on-failure
      RestartSec=10s
      StartLimitBurst=5
      TimeoutStartSec=180s
      [Install]
      WantedBy=multi-user.target

  # Relay monitor script
  - path: /usr/local/bin/relay-monitor.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      # Monitor WireGuard peer count and report metrics
      while true; do
        PEERS=$(wg show wg0 peers 2>/dev/null | wc -l)
        TRANSFER=$(wg show wg0 transfer 2>/dev/null)
        echo ""[$(date)] Active peers: $PEERS"" >> /var/log/decloud-relay-monitor.log
        sleep 30
      done

  # Ready callback script
  - path: /usr/local/bin/relay-notify-ready.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      set -e
      LOG=""/var/log/decloud-relay-callback.log""
      MARKER=""/var/lib/decloud-relay/callback-complete""
      log() { echo ""[$(date +'%Y-%m-%d %H:%M:%S')] $*"" | tee -a ""$LOG""; }
      [ -f ""$MARKER"" ] && { log ""Already completed""; exit 0; }
      log ""Starting relay ready callback...""
      VM_ID=""{{VM_ID}}""; NODE_ID=""{{NODE_ID}}""
      # Wait for WireGuard to come up
      for i in $(seq 1 30); do
        wg show wg0 >/dev/null 2>&1 && { log ""WireGuard up""; break; }
        [ $i -eq 30 ] && { log ""WireGuard timeout""; exit 1; }
        sleep 2
      done
      WG_PUBKEY=$(wg show wg0 public-key 2>/dev/null)
      log ""WireGuard public key: $WG_PUBKEY""
      # Derive relay token from WireGuard private key
      WG_PRIVKEY=$(cat /etc/wireguard/wg0.conf | grep PrivateKey | awk '{print $3}')
      MESSAGE=""${NODE_ID}:${VM_ID}""
      TOKEN=$(echo -n ""$MESSAGE"" | openssl dgst -sha256 -hmac ""$WG_PRIVKEY"" -binary | base64)
      # Notify orchestrator
      GW=$(ip route | grep default | awk '{print $3}' | head -1)
      [ -z ""$GW"" ] && GW=""192.168.122.1""
      AGENT=""http://${GW}:5100""
      log ""Notifying node agent at $AGENT...""
      HTTP=$(curl -X POST ""$AGENT/api/relay/ready"" \
        -H ""Content-Type: application/json"" -H ""X-Relay-Token: $TOKEN"" \
        -d ""{\""nodeId\"": \""$NODE_ID\"", \""relayVmId\"": \""$VM_ID\"", \""wireGuardPublicKey\"": \""$WG_PUBKEY\"", \""wireGuardEndpoint\"": \""{{NODE_PUBLIC_IP}}:{{WIREGUARD_PORT}}\"" }"" \
        --max-time 10 --retry 2 --retry-delay 5 -s -o /dev/null -w ""%{http_code}"" 2>&1)
      [ ""$HTTP"" = ""200"" ] && { log ""Success""; mkdir -p /var/lib/decloud-relay; echo ""done"" > ""$MARKER""; exit 0; }
      log ""Failed (HTTP $HTTP)""; exit 1

  # Health check script
  - path: /usr/local/bin/relay-health-check.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      if wg show wg0 >/dev/null 2>&1; then
        PEERS=$(wg show wg0 peers | wc -l)
        echo ""{""status"":""active"",""peers"":$PEERS}""
        exit 0
      else
        echo '{""status"":""down""}'
        exit 1
      fi

runcmd:
  - systemctl enable qemu-guest-agent && systemctl start qemu-guest-agent
  - mkdir -p /etc/decloud /var/lib/decloud-relay
  - chown -R relay:relay /var/lib/decloud-relay
  # Enable IP forwarding
  - sysctl -w net.ipv4.ip_forward=1
  - echo 'net.ipv4.ip_forward=1' >> /etc/sysctl.d/99-relay.conf
  # Configure nginx
  - rm -f /etc/nginx/sites-enabled/default
  - ln -sf /etc/nginx/sites-available/relay-health /etc/nginx/sites-enabled/relay-health
  - systemctl enable nginx && systemctl restart nginx
  # Start WireGuard
  - systemctl enable wg-quick@wg0
  - systemctl start wg-quick@wg0
  # Start relay services
  - systemctl daemon-reload
  - systemctl enable decloud-relay-monitor && systemctl start decloud-relay-monitor
  - systemctl enable decloud-relay-nat-callback && systemctl start decloud-relay-nat-callback
  - 'echo ""Relay {{VM_ID}} ready at $(date)"" >> /var/log/relay-bootstrap.log'

final_message: ""DeCloud Relay Node {{VM_NAME}} is ready!""
";

    #endregion

    #region BlockStore Template (stub)

    private const string BlockStoreTemplate = @"#cloud-config
# DeCloud BlockStore VM Cloud-Init — distributed block storage
# Rendered by Orchestrator SystemVmCloudInitProvider
# TODO: Implement full block store configuration

hostname: {{HOSTNAME}}
manage_etc_hosts: true

bootcmd:
  - [ sh, -c, 'rm -f /etc/machine-id && systemd-machine-id-setup' ]

disable_root: true

users:
  - name: blockstore
    groups: [sudo]
    sudo: ['ALL=(ALL) NOPASSWD:ALL']
    shell: /bin/bash
    lock_passwd: true
    ssh_authorized_keys: []

ssh_pwauth: false

packages:
  - qemu-guest-agent
  - curl
  - jq

write_files:
  - path: /etc/decloud/blockstore-metadata.json
    permissions: '0644'
    content: |
      {
        ""node_id"": ""{{NODE_ID}}"",
        ""vm_id"": ""{{VM_ID}}"",
        ""vm_name"": ""{{VM_NAME}}"",
        ""region"": ""{{REGION}}"",
        ""created_at"": ""{{TIMESTAMP}}"",
        ""version"": ""1.0""
      }

runcmd:
  - systemctl enable qemu-guest-agent && systemctl start qemu-guest-agent
  - mkdir -p /etc/decloud /var/lib/decloud-blockstore
  - 'echo ""BlockStore {{VM_ID}} ready at $(date)"" >> /var/log/blockstore-bootstrap.log'

final_message: ""DeCloud BlockStore {{VM_NAME}} is ready!""
";

    #endregion

    #region Ingress Template (stub)

    private const string IngressTemplate = @"#cloud-config
# DeCloud Ingress VM Cloud-Init — HTTP/HTTPS ingress gateway
# Rendered by Orchestrator SystemVmCloudInitProvider
# TODO: Implement full ingress configuration (nginx/caddy + auto-TLS)

hostname: {{HOSTNAME}}
manage_etc_hosts: true

bootcmd:
  - [ sh, -c, 'rm -f /etc/machine-id && systemd-machine-id-setup' ]

disable_root: true

users:
  - name: ingress
    groups: [sudo]
    sudo: ['ALL=(ALL) NOPASSWD:ALL']
    shell: /bin/bash
    lock_passwd: true
    ssh_authorized_keys: []

ssh_pwauth: false

packages:
  - qemu-guest-agent
  - nginx
  - curl
  - jq

write_files:
  - path: /etc/decloud/ingress-metadata.json
    permissions: '0644'
    content: |
      {
        ""node_id"": ""{{NODE_ID}}"",
        ""vm_id"": ""{{VM_ID}}"",
        ""vm_name"": ""{{VM_NAME}}"",
        ""region"": ""{{REGION}}"",
        ""node_public_ip"": ""{{NODE_PUBLIC_IP}}"",
        ""created_at"": ""{{TIMESTAMP}}"",
        ""version"": ""1.0""
      }

runcmd:
  - systemctl enable qemu-guest-agent && systemctl start qemu-guest-agent
  - mkdir -p /etc/decloud
  - 'echo ""Ingress {{VM_ID}} ready at $(date)"" >> /var/log/ingress-bootstrap.log'

final_message: ""DeCloud Ingress {{VM_NAME}} is ready!""
";

    #endregion
}
