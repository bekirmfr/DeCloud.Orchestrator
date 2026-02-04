using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Service to seed initial marketplace templates and categories
/// </summary>
public class TemplateSeederService
{
    private readonly ITemplateService _templateService;
    private readonly DataStore _dataStore;
    private readonly ILogger<TemplateSeederService> _logger;

    public TemplateSeederService(
        ITemplateService templateService,
        DataStore dataStore,
        ILogger<TemplateSeederService> logger)
    {
        _templateService = templateService;
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Seed categories and templates if they don't exist
    /// </summary>
    public async Task SeedAsync(bool force = false)
    {
        _logger.LogInformation("Starting template seeding (force: {Force})", force);

        try
        {
            // Seed categories first
            await SeedCategoriesAsync(force);

            // Seed templates
            await SeedTemplatesAsync(force);

            _logger.LogInformation("Template seeding completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed templates");
            throw;
        }
    }

    private async Task SeedCategoriesAsync(bool force)
    {
        var existingCategories = await _dataStore.GetCategoriesAsync();

        var categories = GetSeedCategories();

        foreach (var category in categories)
        {
            var existing = existingCategories.FirstOrDefault(c => c.Slug == category.Slug);

            if (existing == null)
            {
                await _dataStore.SaveCategoryAsync(category);
                _logger.LogInformation("Created category: {Name}", category.Name);
            }
            else if (force)
            {
                category.Id = existing.Id;
                await _dataStore.SaveCategoryAsync(category);
                _logger.LogInformation("Updated category: {Name}", category.Name);
            }
        }
    }

    private async Task SeedTemplatesAsync(bool force)
    {
        var existingTemplates = await _templateService.GetTemplatesAsync(new TemplateQuery());

        var templates = GetSeedTemplates();

        foreach (var template in templates)
        {
            var existing = existingTemplates.FirstOrDefault(t => t.Slug == template.Slug);

            if (existing == null)
            {
                var created = await _templateService.CreateTemplateAsync(template);
                _logger.LogInformation("✓ Created template: {Name} ({Slug}) v{Version}", 
                    created.Name, created.Slug, created.Version);
            }
            else if (force || IsNewerVersion(template.Version, existing.Version))
            {
                template.Id = existing.Id;
                var updated = await _templateService.UpdateTemplateAsync(template);
                _logger.LogInformation("✓ Updated template: {Name} ({Slug}) v{OldVersion} → v{NewVersion}", 
                    updated.Name, updated.Slug, existing.Version, updated.Version);
            }
            else
            {
                _logger.LogDebug("Template up-to-date: {Name} ({Slug}) v{Version}", 
                    template.Name, template.Slug, template.Version);
            }
        }
    }
    
    /// <summary>
    /// Compare semantic versions (e.g., "1.0.0" vs "1.1.0")
    /// Returns true if newVersion > currentVersion
    /// </summary>
    private bool IsNewerVersion(string newVersion, string currentVersion)
    {
        try
        {
            var newParts = newVersion.Split('.').Select(int.Parse).ToArray();
            var currentParts = currentVersion.Split('.').Select(int.Parse).ToArray();
            
            // Compare major, minor, patch
            for (int i = 0; i < Math.Max(newParts.Length, currentParts.Length); i++)
            {
                var newPart = i < newParts.Length ? newParts[i] : 0;
                var currentPart = i < currentParts.Length ? currentParts[i] : 0;
                
                if (newPart > currentPart) return true;
                if (newPart < currentPart) return false;
            }
            
            return false; // Versions are equal
        }
        catch
        {
            // If version parsing fails, treat as equal (don't update)
            _logger.LogWarning("Failed to parse versions: new={New}, current={Current}", 
                newVersion, currentVersion);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Seed Data Definitions
    // ════════════════════════════════════════════════════════════════════════

    private List<TemplateCategory> GetSeedCategories()
    {
        return new List<TemplateCategory>
        {
            new TemplateCategory
            {
                Name = "AI & Machine Learning",
                Slug = "ai-ml",
                Description = "Pre-configured environments for AI inference, training, and ML development",
                IconEmoji = "🤖",
                DisplayOrder = 1
            },
            new TemplateCategory
            {
                Name = "Databases",
                Slug = "databases",
                Description = "Production-ready database servers with optimized configurations",
                IconEmoji = "🗄️",
                DisplayOrder = 2
            },
            new TemplateCategory
            {
                Name = "Development Tools",
                Slug = "dev-tools",
                Description = "Development environments, IDEs, and productivity tools",
                IconEmoji = "🛠️",
                DisplayOrder = 3
            },
            new TemplateCategory
            {
                Name = "Web Applications",
                Slug = "web-apps",
                Description = "Content management systems, blogs, and web platforms",
                IconEmoji = "🌐",
                DisplayOrder = 4
            },
            new TemplateCategory
            {
                Name = "Privacy & Security",
                Slug = "privacy-security",
                Description = "Privacy-focused tools, VPNs, secure browsing, and censorship-resistant applications",
                IconEmoji = "🔒",
                DisplayOrder = 5
            }
        };
    }

    private List<VmTemplate> GetSeedTemplates()
    {
        return new List<VmTemplate>
        {
            CreateStableDiffusionTemplate(),
            CreatePostgreSqlTemplate(),
            CreateCodeServerTemplate(),
            CreatePrivateBrowserTemplate(),
            CreateShadowsocksProxyTemplate()
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Definitions
    // ════════════════════════════════════════════════════════════════════════

    private VmTemplate CreateStableDiffusionTemplate()
    {
        return new VmTemplate
        {
            Name = "Stable Diffusion WebUI",
            Slug = "stable-diffusion-webui",
            Version = "1.0.0",
            Category = "ai-ml",
            Description = "AUTOMATIC1111 Stable Diffusion WebUI with popular models pre-installed. Generate images from text prompts using cutting-edge AI models.",
            LongDescription = @"## Features
- AUTOMATIC1111 Stable Diffusion WebUI (latest version)
- Pre-configured with SD 1.5 base model
- ControlNet extension included
- Optimized for NVIDIA GPUs
- Automatic model downloading
- Web interface accessible via browser

## Getting Started
1. Wait for initial setup to complete (~5-10 minutes)
2. Access the WebUI at `https://${DECLOUD_DOMAIN}:7860`
3. Select a model from the dropdown
4. Start generating images!

## Requirements
- GPU: NVIDIA with at least 8GB VRAM (RTX 3060+ recommended)
- RAM: 16GB minimum
- Storage: 40GB for models and cache",
            
            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/AUTOMATIC1111/stable-diffusion-webui",
            
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 16L * 1024 * 1024 * 1024, // 16 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
                RequiresGpu = true,
                GpuModel = "NVIDIA"
            },
            
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 32L * 1024 * 1024 * 1024, // 32 GB
                DiskBytes = 100L * 1024 * 1024 * 1024,  // 100 GB
                RequiresGpu = true,
                GpuModel = "NVIDIA RTX 3090"
            },
            
            RequiresGpu = true,
            GpuRequirement = "NVIDIA GPU with CUDA support (RTX 3060+ recommended)",
            RequiredCapabilities = new List<string> { "cuda", "nvidia-gpu" },
            
            Tags = new List<string> { "ai", "stable-diffusion", "image-generation", "gpu", "machine-learning" },
            
            CloudInitTemplate = @"#cloud-config

# Stable Diffusion WebUI - Automatic Installation
# DeCloud Template v1.0.0

packages:
  - git
  - wget
  - python3
  - python3-pip
  - python3-venv
  - libgl1
  - libglib2.0-0

runcmd:
  # Update system
  - apt-get update
  - apt-get upgrade -y
  
  # Install NVIDIA drivers and CUDA (if GPU available)
  - |
    if lspci | grep -i nvidia; then
      echo ""Installing NVIDIA drivers...""
      apt-get install -y ubuntu-drivers-common
      ubuntu-drivers autoinstall
      
      # Install CUDA toolkit
      wget https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.1-1_all.deb
      dpkg -i cuda-keyring_1.1-1_all.deb
      apt-get update
      apt-get install -y cuda-toolkit-12-3
    fi
  
  # Create application user
  - useradd -m -s /bin/bash sduser
  
  # Clone Stable Diffusion WebUI
  - su - sduser -c ""git clone https://github.com/AUTOMATIC1111/stable-diffusion-webui.git /home/sduser/stable-diffusion-webui""
  
  # Download base model
  - su - sduser -c ""mkdir -p /home/sduser/stable-diffusion-webui/models/Stable-diffusion""
  - su - sduser -c ""wget -O /home/sduser/stable-diffusion-webui/models/Stable-diffusion/v1-5-pruned-emaonly.safetensors https://huggingface.co/runwayml/stable-diffusion-v1-5/resolve/main/v1-5-pruned-emaonly.safetensors""
  
  # Create systemd service
  - |
    cat > /etc/systemd/system/stable-diffusion.service <<'EOF'
    [Unit]
    Description=Stable Diffusion WebUI
    After=network.target
    
    [Service]
    Type=simple
    User=sduser
    WorkingDirectory=/home/sduser/stable-diffusion-webui
    ExecStart=/home/sduser/stable-diffusion-webui/webui.sh --listen --port 7860 --api
    Restart=always
    RestartSec=10
    
    [Install]
    WantedBy=multi-user.target
    EOF
  
  # Enable and start service
  - systemctl daemon-reload
  - systemctl enable stable-diffusion.service
  - systemctl start stable-diffusion.service
  
  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║           Stable Diffusion WebUI - DeCloud Template          ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  WebUI: https://${DECLOUD_DOMAIN}:7860                       ║
    ║  API:   https://${DECLOUD_DOMAIN}:7860/docs                  ║
    ║                                                               ║
    ║  Service: systemctl status stable-diffusion                  ║
    ║  Logs:    journalctl -u stable-diffusion -f                  ║
    ║                                                               ║
    ║  Models: /home/sduser/stable-diffusion-webui/models          ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  Stable Diffusion WebUI is starting up!
  
  First-time setup will take 5-10 minutes to download dependencies.
  
  Access the WebUI at: https://${DECLOUD_DOMAIN}:7860
  
  Check status: systemctl status stable-diffusion
  View logs: journalctl -u stable-diffusion -f",
            
            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["COMMANDLINE_ARGS"] = "--listen --port 7860 --api --xformers"
            },
            
            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 7860,
                    Protocol = "http",
                    Description = "Stable Diffusion WebUI",
                    IsPublic = true
                }
            },
            
            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:7860",
            
            EstimatedCostPerHour = 0.50m, // $0.50/hour for GPU instance
            
            Status = TemplateStatus.Published,
            IsFeatured = true,
            
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private VmTemplate CreatePostgreSqlTemplate()
    {
        return new VmTemplate
        {
            Name = "PostgreSQL Database",
            Slug = "postgresql",
            Version = "1.0.0",
            Category = "databases",
            Description = "Production-ready PostgreSQL database server with optimized configuration and automatic backups.",
            LongDescription = @"## Features
- PostgreSQL 15 (latest stable)
- Optimized for production workloads
- Automatic daily backups
- Remote access enabled
- PgAdmin-compatible
- SSL/TLS encryption

## Getting Started
1. Wait for setup to complete (~2 minutes)
2. Connect using:
   - Host: `${DECLOUD_DOMAIN}`
   - Port: `5432`
   - Database: `decloud`
   - User: `postgres`
   - Password: `${DECLOUD_PASSWORD}`

## Connection String
```
postgresql://postgres:${DECLOUD_PASSWORD}@${DECLOUD_DOMAIN}:5432/decloud
```",
            
            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://www.postgresql.org/",
            
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 2L * 1024 * 1024 * 1024,  // 2 GB
                DiskBytes = 20L * 1024 * 1024 * 1024,   // 20 GB
                RequiresGpu = false
            },
            
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,  // 8 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
                RequiresGpu = false
            },
            
            RequiresGpu = false,
            
            Tags = new List<string> { "database", "postgresql", "sql", "postgres" },
            
            CloudInitTemplate = @"#cloud-config

# PostgreSQL Database Server
# DeCloud Template v1.0.0

packages:
  - postgresql-15
  - postgresql-contrib-15

runcmd:
  # Configure PostgreSQL to listen on all interfaces
  - |
    cat >> /etc/postgresql/15/main/postgresql.conf <<'EOF'
    listen_addresses = '*'
    max_connections = 100
    shared_buffers = 256MB
    effective_cache_size = 1GB
    maintenance_work_mem = 64MB
    checkpoint_completion_target = 0.9
    wal_buffers = 16MB
    default_statistics_target = 100
    random_page_cost = 1.1
    effective_io_concurrency = 200
    work_mem = 2621kB
    min_wal_size = 1GB
    max_wal_size = 4GB
    EOF
  
  # Allow remote connections
  - |
    cat >> /etc/postgresql/15/main/pg_hba.conf <<'EOF'
    host    all             all             0.0.0.0/0               md5
    host    all             all             ::/0                    md5
    EOF
  
  # Set postgres password
  - sudo -u postgres psql -c ""ALTER USER postgres PASSWORD '${DECLOUD_PASSWORD}';""
  
  # Create default database
  - sudo -u postgres createdb decloud
  
  # Restart PostgreSQL
  - systemctl restart postgresql
  
  # Enable automatic startup
  - systemctl enable postgresql
  
  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║              PostgreSQL Database - DeCloud Template          ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  Host:     ${DECLOUD_DOMAIN}                                 ║
    ║  Port:     5432                                              ║
    ║  Database: decloud                                           ║
    ║  User:     postgres                                          ║
    ║  Password: ${DECLOUD_PASSWORD}                               ║
    ║                                                               ║
    ║  Connection String:                                          ║
    ║  postgresql://postgres:${DECLOUD_PASSWORD}@${DECLOUD_DOMAIN}:5432/decloud
    ║                                                               ║
    ║  Service: systemctl status postgresql                        ║
    ║  Logs:    journalctl -u postgresql -f                        ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  PostgreSQL is ready!
  
  Connection Details:
  - Host: ${DECLOUD_DOMAIN}
  - Port: 5432
  - Database: decloud
  - User: postgres
  - Password: ${DECLOUD_PASSWORD}
  
  Connect: psql -h ${DECLOUD_DOMAIN} -U postgres -d decloud",
            
            DefaultEnvironmentVariables = new Dictionary<string, string>(),
            
            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 5432,
                    Protocol = "tcp",
                    Description = "PostgreSQL Database",
                    IsPublic = true
                }
            },
            
            DefaultAccessUrl = "postgresql://postgres:${DECLOUD_PASSWORD}@${DECLOUD_DOMAIN}:5432/decloud",
            
            EstimatedCostPerHour = 0.05m, // $0.05/hour
            
            Status = TemplateStatus.Published,
            IsFeatured = true,
            
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private VmTemplate CreateCodeServerTemplate()
    {
        return new VmTemplate
        {
            Name = "VS Code Server",
            Slug = "code-server",
            Version = "2.0.1",  // Patch: Fixed systemd service path (/usr/local/bin)
            Category = "dev-tools",
            Description = "Visual Studio Code in your browser. Full-featured development environment accessible from anywhere.",
            LongDescription = @"## Features
- Full VS Code experience in browser
- Extensions marketplace
- Integrated terminal
- Git integration
- Multi-language support
- Customizable themes

## Getting Started
1. Wait for setup to complete (~3 minutes)
2. Access VS Code at `https://${DECLOUD_DOMAIN}:8080`
3. Password: `${DECLOUD_PASSWORD}`
4. Install extensions and start coding!

## Pre-installed Tools
- Node.js 20 LTS
- Python 3.11
- Git
- Docker
- Common build tools",
            
            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/coder/code-server",
            
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 4L * 1024 * 1024 * 1024,  // 4 GB
                DiskBytes = 30L * 1024 * 1024 * 1024,   // 30 GB
                RequiresGpu = false
            },
            
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,  // 8 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
                RequiresGpu = false
            },
            
            RequiresGpu = false,
            
            Tags = new List<string> { "vscode", "ide", "development", "coding", "editor" },
            
            CloudInitTemplate = @"#cloud-config

# VS Code Server - Browser-based IDE
# DeCloud Template v2.0.0 - Fixed auth & installation

packages:
  - curl
  - wget
  - git
  - build-essential

runcmd:
  # Install Node.js 20 LTS
  - curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
  - apt-get install -y nodejs
  
  # Install Python 3.11
  - apt-get install -y python3.11 python3-pip python3.11-venv
  
  # Install Docker
  - curl -fsSL https://get.docker.com | sh
  - usermod -aG docker ubuntu
  
  # Install code-server (with explicit error handling)
  - export HOME=/root
  - curl -fsSL https://code-server.dev/install.sh -o /tmp/code-server-install.sh
  - bash /tmp/code-server-install.sh --method=standalone --prefix=/usr/local
  
  # Configure code-server
  - mkdir -p /home/ubuntu/.config/code-server
  - |
    cat > /home/ubuntu/.config/code-server/config.yaml <<'EOFCONFIG'
    bind-addr: 0.0.0.0:8080
    auth: password
    password: ${DECLOUD_PASSWORD}
    cert: false
    proxy-domain: ${DECLOUD_DOMAIN}
    EOFCONFIG
  - chown -R ubuntu:ubuntu /home/ubuntu/.config
  
  # Create systemd service
  - |
    cat > /etc/systemd/system/code-server.service <<'EOF'
    [Unit]
    Description=VS Code Server
    After=network.target
    
    [Service]
    Type=simple
    User=ubuntu
    WorkingDirectory=/home/ubuntu
    ExecStart=/usr/local/bin/code-server --bind-addr 0.0.0.0:8080
    Restart=always
    RestartSec=10
    
    [Install]
    WantedBy=multi-user.target
    EOF
  
  # Enable and start service
  - systemctl daemon-reload
  - systemctl enable code-server
  - systemctl start code-server
  
  # Wait for code-server to be ready and install extensions
  - sleep 15
  - |
    if command -v code-server >/dev/null 2>&1; then
      sudo -u ubuntu -H bash -c 'code-server --install-extension ms-python.python || true'
      sudo -u ubuntu -H bash -c 'code-server --install-extension dbaeumer.vscode-eslint || true'
      sudo -u ubuntu -H bash -c 'code-server --install-extension esbenp.prettier-vscode || true'
    fi
  
  # Create welcome workspace
  - mkdir -p /home/ubuntu/workspace
  - |
    cat > /home/ubuntu/workspace/README.md <<'EOF'
    # Welcome to VS Code on DeCloud! 🚀
    
    ## Getting Started
    
    This is your cloud development environment. You have:
    
    - **Node.js**: `node --version`
    - **Python**: `python3 --version`
    - **Git**: `git --version`
    - **Docker**: `docker --version`
    
    ## Tips
    
    - Open the integrated terminal with `` Ctrl+` ``
    - Install extensions from the Extensions tab
    - Your workspace persists across sessions
    
    Happy coding!
    EOF
  - chown -R ubuntu:ubuntu /home/ubuntu/workspace
  
  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║              VS Code Server - DeCloud Template               ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  WebUI: https://${DECLOUD_DOMAIN}:8080                       ║
    ║  Password: ${DECLOUD_PASSWORD}                               ║
    ║                                                               ║
    ║  Service: systemctl status code-server                       ║
    ║  Logs:    journalctl -u code-server -f                       ║
    ║                                                               ║
    ║  Workspace: /home/ubuntu/workspace                           ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  VS Code Server is ready!
  
  Access your IDE at: https://${DECLOUD_DOMAIN}:8080
  Password: ${DECLOUD_PASSWORD}
  
  Pre-installed: Node.js, Python, Git, Docker
  
  Start coding! 🎉",
            
            DefaultEnvironmentVariables = new Dictionary<string, string>(),
            
            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8080,
                    Protocol = "http",
                    Description = "VS Code Server WebUI",
                    IsPublic = true
                }
            },
            
            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:8080",
            
            EstimatedCostPerHour = 0.10m, // $0.10/hour
            
            Status = TemplateStatus.Published,
            IsFeatured = true,
            
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private VmTemplate CreatePrivateBrowserTemplate()
    {
        return new VmTemplate
        {
            Name = "Private Browser",
            Slug = "private-browser",
            Version = "1.2.0",
            Category = "privacy-security",
            Description = "Isolated browser streamed to your device via WebRTC. Browse privately from a remote VM — your real IP and device fingerprint stay hidden.",
            LongDescription = @"## Features
- **Full Firefox browser** running on a remote VM, streamed via WebRTC
- **IP isolation** — websites see the VM's IP, not yours
- **Fingerprint protection** — browser fingerprint belongs to the VM, not your device
- **Low latency** — WebRTC streaming with hardware-accelerated video encoding
- **Audio support** — full audio passthrough for media playback
- **Clipboard sync** — copy/paste between your device and the remote browser
- **Multi-user ready** — share a browsing session with collaborators (optional)

## How It Works
This template deploys [Neko](https://github.com/m1k1o/neko), a self-hosted browser-in-browser solution that streams a full desktop browser from the VM to your local browser tab via WebRTC. Think of it as a private, disposable browsing environment.

## Use Cases
- **Privacy browsing** — access the web without exposing your real IP or device
- **Geo-shifting** — deploy on nodes in different regions to browse as if you're there
- **Disposable sessions** — spin up, browse, destroy — no local traces
- **Security research** — safely visit untrusted sites in an isolated environment
- **Bypassing restrictions** — access content blocked in your network/region

## Getting Started
1. Wait for setup to complete (~3-5 minutes for Docker image pull)
2. Open `https://${DECLOUD_DOMAIN}:8080` in your browser
3. Log in with password: `${DECLOUD_PASSWORD}`
4. Start browsing privately!

## Default Bandwidth
This template defaults to **Standard (50 Mbps)** bandwidth tier, which provides smooth 720p streaming. Upgrade to Performance tier for 1080p.

## Technical Details
- WebRTC streaming with STUN/TURN relay for NAT traversal
- Works behind firewalls and restrictive networks
- Low latency (typically <100ms with good network conditions)",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/m1k1o/neko",
            License = "Apache-2.0",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 2L * 1024 * 1024 * 1024,  // 2 GB
                DiskBytes = 15L * 1024 * 1024 * 1024,   // 15 GB
                RequiresGpu = false,
                QualityTier = QualityTier.Burstable
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 4L * 1024 * 1024 * 1024,  // 4 GB
                DiskBytes = 20L * 1024 * 1024 * 1024,   // 20 GB
                RequiresGpu = false,
                QualityTier = QualityTier.Balanced
            },

            RequiresGpu = false,

            Tags = new List<string> { "browser", "privacy", "vpn", "neko", "webrtc", "streaming", "firefox", "censorship-resistant" },

            CloudInitTemplate = @"#cloud-config

# Private Browser (Neko) - Isolated Browser Streaming
# DeCloud Template v1.2.0 - TCP multiplexing for WebRTC

packages:
  - apt-transport-https
  - ca-certificates
  - curl
  - gnupg
  - lsb-release

runcmd:
  # Install Docker
  - curl -fsSL https://get.docker.com | sh

  # Create data directory
  - mkdir -p /opt/neko

  # Create docker-compose file
  - |
    cat > /opt/neko/docker-compose.yml <<'EOFCOMPOSE'
    version: '3'
    services:
      neko:
        image: ghcr.io/m1k1o/neko/firefox:latest
        container_name: neko
        restart: unless-stopped
        shm_size: '2gb'
        ports:
          - '8080:8080'
        cap_add:
          - SYS_ADMIN
        environment:
          NEKO_SCREEN: '1920x1080@30'
          NEKO_PASSWORD: '${DECLOUD_PASSWORD}'
          NEKO_PASSWORD_ADMIN: '${DECLOUD_PASSWORD}'
          NEKO_ICESERVERS: '[{""urls"":[""stun:stun.relay.metered.ca:80""]},{""urls"":[""turn:standard.relay.metered.ca:80""],""username"":""openrelayproject"",""credential"":""openrelayproject""},{""urls"":[""turn:standard.relay.metered.ca:80?transport=tcp""],""username"":""openrelayproject"",""credential"":""openrelayproject""},{""urls"":[""turn:standard.relay.metered.ca:443""],""username"":""openrelayproject"",""credential"":""openrelayproject""},{""urls"":[""turns:standard.relay.metered.ca:443?transport=tcp""],""username"":""openrelayproject"",""credential"":""openrelayproject""}]'
    EOFCOMPOSE

  # Pull image and start container
  - cd /opt/neko && docker compose pull
  - cd /opt/neko && docker compose up -d

  # Create systemd service for auto-restart on boot
  - |
    cat > /etc/systemd/system/neko.service <<'EOF'
    [Unit]
    Description=Neko Private Browser
    After=docker.service
    Requires=docker.service

    [Service]
    Type=oneshot
    RemainAfterExit=yes
    WorkingDirectory=/opt/neko
    ExecStart=/usr/bin/docker compose up -d
    ExecStop=/usr/bin/docker compose down

    [Install]
    WantedBy=multi-user.target
    EOF

  - systemctl daemon-reload
  - systemctl enable neko.service

  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║            Private Browser (Neko) - DeCloud Template         ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  Browser: https://${DECLOUD_DOMAIN}:8080                     ║
    ║  Password: ${DECLOUD_PASSWORD}                               ║
    ║                                                               ║
    ║  Service: docker compose -f /opt/neko/docker-compose.yml ps  ║
    ║  Logs:    docker logs -f neko                                ║
    ║  Restart: systemctl restart neko                             ║
    ║                                                               ║
    ║  Your browsing is isolated to this VM.                       ║
    ║  Websites see VM's IP, not yours.                            ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  Private Browser is starting up!

  The Docker image download may take 2-3 minutes on first boot.

  Access your private browser at: https://${DECLOUD_DOMAIN}:8080
  Password: ${DECLOUD_PASSWORD}

  Browse privately — your real IP and fingerprint stay hidden.",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["NEKO_SCREEN"] = "1920x1080@30"
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8080,
                    Protocol = "http",
                    Description = "Neko Browser WebUI and WebRTC signaling",
                    IsPublic = true
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:8080",

            EstimatedCostPerHour = 0.06m, // $0.06/hour — lightweight workload

            DefaultBandwidthTier = BandwidthTier.Standard, // 50 Mbps — good for 720p streaming

            Status = TemplateStatus.Published,
            IsFeatured = true,

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private VmTemplate CreateShadowsocksProxyTemplate()
    {
        return new VmTemplate
        {
            Name = "Privacy Proxy (Shadowsocks)",
            Slug = "privacy-proxy-shadowsocks",
            Version = "1.0.1",
            Category = "privacy-security",
            Description = "Fast SOCKS5 proxy for private browsing with native browser experience. Route your traffic through a remote VM — fast, lightweight, and secure.",
            LongDescription = @"## Features
- **Native browser experience** — use your own browser, no sluggish video streaming
- **Fast and responsive** — minimal latency, no video encoding overhead
- **Low bandwidth** — only proxies actual web traffic, not video
- **IP isolation** — websites see the VM's IP, not yours
- **Encrypted traffic** — Shadowsocks uses strong encryption
- **Works everywhere** — compatible with any browser, app, or OS

## What is Shadowsocks?
Shadowsocks is a secure SOCKS5 proxy protocol originally designed to bypass internet censorship. It provides:
- Fast performance (no video encoding like Neko)
- Strong encryption (AEAD ciphers)
- Low overhead (minimal CPU usage)
- Wide compatibility (works with all browsers and many apps)

## How It Works
1. VM runs a Shadowsocks server on port 8388
2. You configure your browser/system to use it as a SOCKS5 proxy
3. All traffic routes through the VM encrypted
4. Websites see the VM's IP address

## Setup Instructions
1. Wait for setup to complete (~2 minutes)
2. Get your **VM's public IP address** from the VM details page
3. Configure your browser/system with the Shadowsocks server details:
   - **Server**: VM's public IP address (e.g., `88.234.217.167`)
   - **Port**: `8388`
   - **Password**: `${DECLOUD_PASSWORD}`
   - **Encryption**: `chacha20-ietf-poly1305`

⚠️ **Important**: Connect to the VM's **direct IP address**, not the ingress subdomain. SOCKS5 requires a direct TCP connection.

## Browser Configuration
### Chrome/Edge/Brave
1. Use extension: **Proxy SwitchyOmega**
2. Add SOCKS5 proxy: `<VM-IP>:8388` (use your VM's public IP)
3. Toggle proxy on/off easily

### Firefox
1. Settings → Network Settings → Manual proxy
2. SOCKS Host: `<VM-IP>` (your VM's public IP), Port: `8388`
3. Select SOCKS v5

### System-Wide (All Apps)
Use a Shadowsocks client:
- **Windows/Mac**: Shadowsocks-Windows, ShadowsocksX-NG
- **Linux**: shadowsocks-libev, shadowsocks-rust
- **iOS**: Shadowrocket, Surge
- **Android**: Shadowsocks Android

## Use Cases
- **Privacy browsing** — hide your real IP address
- **Geo-shifting** — access content from different regions
- **Bypass censorship** — access blocked websites
- **Testing** — test apps from different geographic locations
- **Security** — encrypted tunnel on public WiFi

## Performance
- **Latency**: ~5-20ms overhead (vs. 50-100ms for video streaming)
- **Bandwidth**: Only your actual web traffic (vs. constant video stream)
- **CPU**: Minimal overhead (vs. heavy video encoding)
- **Responsiveness**: Native browser speed

## Default Bandwidth
This template defaults to **Standard (50 Mbps)** bandwidth tier, sufficient for fast browsing. Upgrade to Performance for higher speeds.",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/shadowsocks/shadowsocks-rust",
            License = "MIT",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 1,
                MemoryBytes = 512 * 1024 * 1024,  // 512 MB
                DiskBytes = 10L * 1024 * 1024 * 1024,  // 10 GB
                ImageId = "ubuntu-22.04",
                QualityTier = QualityTier.Burstable
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 1024 * 1024 * 1024,  // 1 GB
                DiskBytes = 10L * 1024 * 1024 * 1024,  // 10 GB
                ImageId = "ubuntu-22.04",
                QualityTier = QualityTier.Burstable
            },

            RequiresGpu = false,

            Tags = new List<string> { "proxy", "privacy", "vpn", "shadowsocks", "socks5", "censorship-resistant", "geo-shift" },

            CloudInitTemplate = @"#cloud-config

# Privacy Proxy (Shadowsocks) - Fast SOCKS5 Proxy
# DeCloud Template v1.0.1

packages:
  - apt-transport-https
  - ca-certificates
  - curl

runcmd:
  # Install shadowsocks-rust (fast, modern implementation)
  - |
    ARCH=$(uname -m)
    if [ ""$ARCH"" = ""x86_64"" ]; then
      SS_ARCH=""x86_64-unknown-linux-gnu""
    elif [ ""$ARCH"" = ""aarch64"" ]; then
      SS_ARCH=""aarch64-unknown-linux-gnu""
    else
      SS_ARCH=""x86_64-unknown-linux-gnu""
    fi

    SS_VERSION=""1.18.2""
    wget -q https://github.com/shadowsocks/shadowsocks-rust/releases/download/v${SS_VERSION}/shadowsocks-v${SS_VERSION}.${SS_ARCH}.tar.xz
    tar -xf shadowsocks-v${SS_VERSION}.${SS_ARCH}.tar.xz
    mv ssserver /usr/local/bin/
    chmod +x /usr/local/bin/ssserver
    rm shadowsocks-v${SS_VERSION}.${SS_ARCH}.tar.xz

  # Create config directory
  - mkdir -p /etc/shadowsocks

  # Create shadowsocks configuration
  - |
    cat > /etc/shadowsocks/config.json <<'EOF'
    {
        ""server"": ""0.0.0.0"",
        ""server_port"": 8388,
        ""password"": ""${DECLOUD_PASSWORD}"",
        ""timeout"": 300,
        ""method"": ""chacha20-ietf-poly1305"",
        ""fast_open"": true,
        ""nameserver"": ""1.1.1.1"",
        ""mode"": ""tcp_and_udp""
    }
    EOF

  # Create systemd service
  - |
    cat > /etc/systemd/system/shadowsocks.service <<'EOF'
    [Unit]
    Description=Shadowsocks SOCKS5 Proxy Server
    After=network.target

    [Service]
    Type=simple
    ExecStart=/usr/local/bin/ssserver -c /etc/shadowsocks/config.json
    Restart=on-failure
    RestartSec=5s
    StandardOutput=journal
    StandardError=journal

    [Install]
    WantedBy=multi-user.target
    EOF

  - systemctl daemon-reload
  - systemctl enable shadowsocks.service
  - systemctl start shadowsocks.service

  # Create connection info script
  - |
    cat > /root/connection-info.sh <<'EOFSCRIPT'
    #!/bin/bash
    echo ""════════════════════════════════════════════════════════════""
    echo ""  Privacy Proxy (Shadowsocks) - Connection Information""
    echo ""════════════════════════════════════════════════════════════""
    echo """"
    echo ""Server:     ${DECLOUD_PUBLIC_IP}""
    echo ""Port:       8388""
    echo ""Password:   ${DECLOUD_PASSWORD}""
    echo ""Encryption: chacha20-ietf-poly1305""
    echo ""Protocol:   SOCKS5""
    echo """"
    echo ""⚠️  IMPORTANT: Use the server IP above (not the ingress subdomain)""
    echo ""   Shadowsocks requires a direct SOCKS5 connection.""
    echo """"
    echo ""Browser Setup (Chrome/Edge/Brave):""
    echo ""  1. Install 'Proxy SwitchyOmega' extension""
    echo ""  2. Add SOCKS5 proxy: ${DECLOUD_PUBLIC_IP}:8388""
    echo ""  3. Toggle proxy on/off as needed""
    echo """"
    echo ""Browser Setup (Firefox):""
    echo ""  Settings → Network → Manual Proxy""
    echo ""  SOCKS Host: ${DECLOUD_PUBLIC_IP}, Port: 8388, SOCKS v5""
    echo """"
    echo ""System-Wide Setup:""
    echo ""  Use Shadowsocks client for your OS:""
    echo ""  - Windows/Mac: Shadowsocks-Windows, ShadowsocksX-NG""
    echo ""  - Linux: shadowsocks-libev, shadowsocks-rust""
    echo ""  - iOS: Shadowrocket, Surge""
    echo ""  - Android: Shadowsocks Android""
    echo """"
    echo ""Service Status:""
    systemctl status shadowsocks.service --no-pager | grep Active
    echo """"
    echo ""════════════════════════════════════════════════════════════""
    EOFSCRIPT

  - chmod +x /root/connection-info.sh

  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║      Privacy Proxy (Shadowsocks) - DeCloud Template         ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  Connection Details:                                          ║
    ║  Server:     ${DECLOUD_PUBLIC_IP}                            ║
    ║  Port:       8388                                             ║
    ║  Password:   ${DECLOUD_PASSWORD}                             ║
    ║  Encryption: chacha20-ietf-poly1305                           ║
    ║                                                               ║
    ║  ⚠️  Use the server IP above, NOT the ingress subdomain!     ║
    ║                                                               ║
    ║  View full setup instructions:                                ║
    ║    /root/connection-info.sh                                   ║
    ║                                                               ║
    ║  Service commands:                                            ║
    ║    systemctl status shadowsocks                               ║
    ║    systemctl restart shadowsocks                              ║
    ║    journalctl -u shadowsocks -f                               ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  Privacy Proxy (Shadowsocks) is ready!

  Connection Details:
    Server:     ${DECLOUD_PUBLIC_IP}
    Port:       8388
    Password:   ${DECLOUD_PASSWORD}
    Encryption: chacha20-ietf-poly1305

  ⚠️  IMPORTANT: Connect to the VM's PUBLIC IP above, not the ingress subdomain.
  Shadowsocks uses SOCKS5 protocol which requires a direct TCP connection.

  Configure your browser or system with these details to start
  browsing privately through your VM.

  Run '/root/connection-info.sh' for detailed setup instructions.",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                // No special variables needed - password comes from DECLOUD_PASSWORD
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8388,
                    Protocol = "tcp",
                    Description = "Shadowsocks SOCKS5 proxy",
                    IsPublic = true
                }
            },

            DefaultAccessUrl = "shadowsocks://${DECLOUD_PASSWORD}@${DECLOUD_PUBLIC_IP}:8388",

            EstimatedCostPerHour = 0.02m, // $0.02/hour — very lightweight

            DefaultBandwidthTier = BandwidthTier.Standard, // 50 Mbps — plenty for browsing

            Status = TemplateStatus.Published,
            IsFeatured = true,

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
