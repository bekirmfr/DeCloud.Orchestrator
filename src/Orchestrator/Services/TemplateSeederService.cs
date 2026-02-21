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
            CreateStableDiffusionCpuTemplate(),
            CreatePostgreSqlTemplate(),
            CreateCodeServerTemplate(),
            CreatePrivateBrowserTemplate(),
            CreateShadowsocksProxyTemplate(),
            CreateWebProxyBrowserTemplate(),
            CreateOllamaOpenWebUiTemplate(),
            CreateVllmInferenceServerTemplate()
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Definitions
    // ════════════════════════════════════════════════════════════════════════

    private VmTemplate CreateStableDiffusionTemplate()
    {
        return new VmTemplate
        {
            Name = "Stable Diffusion WebUI Forge",
            Slug = "stable-diffusion-webui",
            Version = "2.0.0",
            Category = "ai-ml",
            Description = "Stable Diffusion WebUI Forge with popular models pre-installed. Generate images from text prompts using cutting-edge AI models.",
            LongDescription = @"## Features
- Stable Diffusion WebUI Forge (actively maintained fork of AUTOMATIC1111)
- Pre-configured with SD 1.5 base model
- Optimized for NVIDIA GPUs with better memory management
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
            SourceUrl = "https://github.com/lllyasviel/stable-diffusion-webui-forge",

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

            Tags = new List<string> { "ai", "stable-diffusion", "image-generation", "gpu", "machine-learning", "forge" },

            CloudInitTemplate = @"#cloud-config

# Stable Diffusion WebUI Forge (GPU) - Automatic Installation
# DeCloud Template v2.0.0 — Uses Forge (maintained AUTOMATIC1111 fork)

packages:
  - git
  - wget
  - python3
  - python3-pip
  - python3-venv
  - python3-setuptools
  - pkg-config
  - libcairo2-dev
  - libgirepository1.0-dev
  - libgl1
  - libglib2.0-0
  - qemu-guest-agent

runcmd:
  - systemctl enable --now qemu-guest-agent

  # Update package index (no full upgrade — keeps boot fast)
  - apt-get update

  # Install NVIDIA drivers and CUDA (if GPU available)
  - |
    if lspci | grep -i nvidia; then
      echo ""Installing NVIDIA drivers...""
      apt-get install -y ubuntu-drivers-common
      ubuntu-drivers autoinstall

      # Install CUDA toolkit
      apt-get install -y --no-install-recommends nvidia-cuda-toolkit
    fi

  # Create application user
  - useradd -m -s /bin/bash sduser

  # Clone Stable Diffusion WebUI Forge
  - su - sduser -c ""git clone https://github.com/lllyasviel/stable-diffusion-webui-forge.git /home/sduser/stable-diffusion-webui""

  # Pre-create venv and install build tools.
  # Pip build-isolation downloads latest setuptools (>=71) which removed pkg_resources.
  # Pre-installing wheel+setuptools avoids build failures for packages that need them.
  - su - sduser -c ""python3 -m venv /home/sduser/stable-diffusion-webui/venv""
  - su - sduser -c ""/home/sduser/stable-diffusion-webui/venv/bin/pip install wheel setuptools""

  # Download base model (with retry)
  - su - sduser -c ""mkdir -p /home/sduser/stable-diffusion-webui/models/Stable-diffusion""
  - su - sduser -c ""wget --tries=3 --retry-connrefused --waitretry=5 -O /home/sduser/stable-diffusion-webui/models/Stable-diffusion/v1-5-pruned-emaonly.safetensors https://huggingface.co/runwayml/stable-diffusion-v1-5/resolve/main/v1-5-pruned-emaonly.safetensors""

  # Create systemd service
  - |
    cat > /etc/systemd/system/stable-diffusion.service <<'EOF'
    [Unit]
    Description=Stable Diffusion WebUI Forge
    After=network.target

    [Service]
    Type=simple
    User=sduser
    Environment=HOME=/home/sduser
    WorkingDirectory=/home/sduser/stable-diffusion-webui
    ExecStart=/home/sduser/stable-diffusion-webui/webui.sh --listen --port 7860 --api --gradio-auth user:${DECLOUD_PASSWORD}
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
    ║       Stable Diffusion WebUI Forge - DeCloud Template        ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  WebUI: https://${DECLOUD_DOMAIN}:7860                       ║
    ║  User:  user / ${DECLOUD_PASSWORD}                           ║
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
  Stable Diffusion WebUI Forge is starting up!

  First-time setup will take 5-10 minutes to download dependencies.

  Access the WebUI at: https://${DECLOUD_DOMAIN}:7860
  Username: user / Password: ${DECLOUD_PASSWORD}

  Check status: systemctl status stable-diffusion
  View logs: journalctl -u stable-diffusion -f",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["COMMANDLINE_ARGS"] = "--listen --port 7860 --api"
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 7860,
                    Protocol = "http",
                    Description = "Stable Diffusion WebUI",
                    IsPublic = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/sdapi/v1/sd-models",
                        TimeoutSeconds = 900 // GPU model loading + first-time pip install
                    }
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:7860",

            EstimatedCostPerHour = 0.50m, // $0.50/hour for GPU instance

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,
            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

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
                    IsPublic = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.ExecCommand,
                        ExecCommand = "pg_isready -U postgres",
                        TimeoutSeconds = 120
                    }
                }
            },

            DefaultAccessUrl = "postgresql://postgres:${DECLOUD_PASSWORD}@${DECLOUD_DOMAIN}:5432/decloud",

            EstimatedCostPerHour = 0.05m, // $0.05/hour

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,
            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

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
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,
            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

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
            Version = "2.0.0",  // Updated to leverage TCP+UDP Smart Port Allocation
            Category = "privacy-security",
            Description = "Fast SOCKS5 proxy for private browsing with native browser experience. Route your traffic through a remote VM — fast, lightweight, and secure. Now with UDP support for better performance.",
            LongDescription = @"## Features
- **Native browser experience** — use your own browser, no sluggish video streaming
- **Fast and responsive** — minimal latency, no video encoding overhead
- **UDP support** — improved performance for DNS queries and other protocols (requires Smart Port Allocation)
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
- **TCP+UDP support** for optimal browsing experience

## How It Works
1. VM runs a Shadowsocks server on port 8388
2. **Port is allocated with TCP+UDP protocols** via DeCloud's Smart Port Allocation
3. You configure your browser/system to use it as a SOCKS5 proxy
4. All traffic routes through the VM encrypted
5. Websites see the VM's IP address

## Setup Instructions
1. Wait for setup to complete (~2 minutes)
2. **Allocate port 8388 with BOTH TCP and UDP protocols** (done automatically during VM creation)
3. Get connection details from the welcome message (SSH into VM)
4. Configure your browser/system with the Shadowsocks server details:
   - **Server**: `${DECLOUD_DOMAIN}` (or VM's public IP)
   - **Port**: `8388` (or assigned public port)
   - **Password**: `${DECLOUD_PASSWORD}`
   - **Encryption**: `chacha20-ietf-poly1305`

## Browser Configuration
### Chrome/Edge/Brave
1. Use extension: **Proxy SwitchyOmega**
2. Add SOCKS5 proxy: `${DECLOUD_DOMAIN}:8388`
3. Toggle proxy on/off easily

### Firefox
1. Settings → Network Settings → Manual proxy
2. SOCKS Host: `${DECLOUD_DOMAIN}`, Port: `8388`
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
- **Bypass censorship** — access blocked websites (works through CGNAT with relay nodes!)
- **Testing** — test apps from different geographic locations
- **Security** — encrypted tunnel on public WiFi

## Performance
- **Latency**: ~5-20ms overhead (vs. 50-100ms for video streaming)
- **Bandwidth**: Only your actual web traffic (vs. constant video stream)
- **CPU**: Minimal overhead (vs. heavy video encoding)
- **Responsiveness**: Native browser speed
- **UDP**: Faster DNS resolution and improved app compatibility

## CGNAT Support
This template works seamlessly with nodes behind CGNAT through DeCloud's 3-hop port forwarding:
- Traffic flows: Client → Relay Node → CGNAT Node → VM
- Both TCP and UDP protocols are forwarded correctly
- No configuration changes needed — it just works!

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

            Tags = new List<string> { "proxy", "privacy", "vpn", "shadowsocks", "socks5", "censorship-resistant", "geo-shift", "udp", "tcp" },

            CloudInitTemplate = @"#cloud-config

# Privacy Proxy (Shadowsocks) - Fast SOCKS5 Proxy
# DeCloud Template v2.0.0 - Now with TCP+UDP support via Smart Port Allocation

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

  # Create shadowsocks configuration with TCP+UDP support
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
    Description=Shadowsocks SOCKS5 Proxy Server (TCP+UDP)
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
    echo ""Server:     ${DECLOUD_DOMAIN}""
    echo ""Port:       8388 (TCP+UDP)""
    echo ""Password:   ${DECLOUD_PASSWORD}""
    echo ""Encryption: chacha20-ietf-poly1305""
    echo ""Protocol:   SOCKS5 (TCP+UDP)""
    echo """"
    echo ""✅ Port allocated with TCP+UDP via Smart Port Allocation""
    echo """"
    echo ""Browser Setup (Chrome/Edge/Brave):""
    echo ""  1. Install 'Proxy SwitchyOmega' extension""
    echo ""  2. Add SOCKS5 proxy: ${DECLOUD_DOMAIN}:8388""
    echo ""  3. Toggle proxy on/off as needed""
    echo """"
    echo ""Browser Setup (Firefox):""
    echo ""  Settings → Network → Manual Proxy""
    echo ""  SOCKS Host: ${DECLOUD_DOMAIN}, Port: 8388, SOCKS v5""
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
    echo ""Check TCP listening: netstat -tln | grep 8388""
    echo ""Check UDP listening: netstat -uln | grep 8388""
    echo """"
    echo ""════════════════════════════════════════════════════════════""
    EOFSCRIPT

  - chmod +x /root/connection-info.sh

  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║      Privacy Proxy (Shadowsocks) - DeCloud Template         ║
    ║                 v2.0.0 with TCP+UDP Support                  ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  Connection Details:                                          ║
    ║  Server:     ${DECLOUD_DOMAIN}                               ║
    ║  Port:       8388 (TCP+UDP)                                   ║
    ║  Password:   ${DECLOUD_PASSWORD}                             ║
    ║  Encryption: chacha20-ietf-poly1305                           ║
    ║                                                               ║
    ║  ✅ UDP enabled for improved DNS and app performance          ║
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
  Privacy Proxy (Shadowsocks) is ready with TCP+UDP support!

  Connection Details:
    Server:     ${DECLOUD_DOMAIN}
    Port:       8388 (TCP+UDP)
    Password:   ${DECLOUD_PASSWORD}
    Encryption: chacha20-ietf-poly1305

  ✅ UDP enabled for better DNS resolution and app compatibility
  
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
                    Protocol = "both",  // ✅ CRITICAL: Now uses TCP+UDP!
                    Description = "Shadowsocks SOCKS5 proxy (TCP+UDP)",
                    IsPublic = true
                }
            },

            DefaultAccessUrl = "shadowsocks://${DECLOUD_PASSWORD}@${DECLOUD_DOMAIN}:8388",

            EstimatedCostPerHour = 0.02m, // $0.02/hour — very lightweight

            DefaultBandwidthTier = BandwidthTier.Standard, // 50 Mbps — plenty for browsing

            Status = TemplateStatus.Published,
            IsFeatured = true,

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private VmTemplate CreateWebProxyBrowserTemplate()
    {
        return new VmTemplate
        {
            Name = "Web Proxy Browser",
            Slug = "web-proxy-browser",
            Version = "3.0.0",
            Category = "privacy-security",
            Description = "Private web browsing directly in your browser — no extensions, no client apps. Type any URL and browse through the VM. Lightweight, fast, zero setup.",
            LongDescription = @"## Features
- **Full URL browsing** — type any URL and browse the web through the VM
- **Zero setup** — just open the link and start browsing, no client software needed
- **Lightweight** — no video streaming, no desktop environment, minimal overhead
- **IP isolation** — websites see the VM's IP, not yours
- **Modern web support** — handles JavaScript-heavy sites, CSS, WebSockets via service workers
- **Search integration** — use the search bar to search via DuckDuckGo or enter a URL directly
- **Password protected** — only you can access your browsing session

## How It Works
This template runs [Ultraviolet](https://github.com/titaniumnetwork-dev/Ultraviolet), a modern web proxy that uses service workers to intercept all browser requests and route them through the VM. Unlike video-streaming solutions (Neko), this proxies the actual HTML/CSS/JS — giving you native browser speed with none of the latency.

```
You → Open URL → Ultraviolet (on VM) → fetches page → renders in your browser
```

All traffic exits from the VM's IP address. Your real IP is never exposed to the sites you visit.

## Use Cases
- **Private browsing** — browse without exposing your IP or device fingerprint
- **Geo-shifting** — deploy on nodes in different regions to access geo-restricted content
- **Bypass restrictions** — access blocked sites through the VM's network
- **Quick & disposable** — spin up, browse, destroy — no traces on your device
- **Low-bandwidth friendly** — only transfers HTML/CSS/JS, not video streams

## Getting Started
1. Wait for setup to complete (~2-3 minutes)
2. Open `https://${DECLOUD_DOMAIN}:8080` in your browser
3. Enter the password (username: `user`, password: `${DECLOUD_PASSWORD}`)
4. Type a URL or search term and start browsing!

## Comparison with Other Privacy Templates

| Feature | Web Proxy Browser | Private Browser (Neko) | Shadowsocks |
|---------|------------------|----------------------|-------------|
| Setup required | None | None | Client app needed |
| Resource usage | Very low (1 vCPU, 512MB) | High (2-4 vCPU, 2-4GB) | Very low |
| Latency | Native speed | 50-100ms (video) | Native speed |
| Works with all sites | Most sites | All sites | All sites |
| Bandwidth usage | Low (HTML/CSS/JS only) | High (video stream) | Low |

## Default Bandwidth
This template defaults to **Standard (50 Mbps)** bandwidth tier, more than enough for proxied browsing.

## Technical Details
- Ultraviolet v3 web proxy with Wisp transport and epoxy backend
- Nginx reverse proxy with basic auth on port 8080
- Service worker-based request interception
- Systemd services for automatic startup",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/titaniumnetwork-dev/Ultraviolet-App",
            License = "GPL-3.0",

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
                VirtualCpuCores = 1,
                MemoryBytes = 1024 * 1024 * 1024,  // 1 GB
                DiskBytes = 10L * 1024 * 1024 * 1024,  // 10 GB
                ImageId = "ubuntu-22.04",
                QualityTier = QualityTier.Burstable
            },

            RequiresGpu = false,

            Tags = new List<string> { "browser", "privacy", "proxy", "web-proxy", "censorship-resistant", "ultraviolet", "zero-setup", "lightweight" },

            CloudInitTemplate = @"#cloud-config

# Web Proxy Browser (Ultraviolet) - Private Browsing Proxy
# DeCloud Template v3.0.0 - Cross-Origin Isolation fix for epoxy transport

packages:
  - curl
  - wget
  - git
  - nginx
  - apache2-utils

runcmd:
  # Install Node.js 22 LTS
  - curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
  - apt-get install -y nodejs

  # Clone official Ultraviolet-App
  - git clone https://github.com/titaniumnetwork-dev/Ultraviolet-App.git /opt/webproxy

  # Install dependencies
  - cd /opt/webproxy && npm install --ignore-engines 2>&1 || true

  # Replace frontend with custom branded landing page
  # Uses same element IDs as UV-App so existing JS (index.js, search.js) works
  - |
    cat > /opt/webproxy/public/index.html <<'EOFHTML'
    <!doctype html>
    <html>
    <head>
      <meta charset=""utf-8"">
      <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
      <title>Private Browser</title>
      <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
          font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
          background: #0a0a0a;
          color: #e0e0e0;
          min-height: 100vh;
          display: flex;
          flex-direction: column;
          align-items: center;
          justify-content: center;
        }
        .container { text-align: center; max-width: 600px; width: 90%; }
        h1 { font-size: 2rem; margin-bottom: 0.5rem; color: #fff; }
        .subtitle { color: #888; margin-bottom: 2rem; font-size: 0.95rem; }
        #uv-form {
          display: flex;
          background: #1a1a1a;
          border: 1px solid #333;
          border-radius: 8px;
          overflow: hidden;
          transition: border-color 0.2s;
        }
        #uv-form:focus-within { border-color: #4a9eff; }
        #uv-address {
          flex: 1; padding: 14px 18px; background: transparent;
          border: none; color: #fff; font-size: 1rem; outline: none;
        }
        #uv-address::placeholder { color: #555; }
        #uv-form button {
          padding: 14px 24px; background: #4a9eff; border: none;
          color: #fff; font-size: 1rem; cursor: pointer; transition: background 0.2s;
        }
        #uv-form button:hover { background: #3a8eef; }
        .info { margin-top: 2rem; font-size: 0.85rem; color: #555; }
        #uv-error { color: #ff4444; margin-top: 1rem; font-size: 0.9rem; }
        #uv-error-code { color: #888; font-size: 0.8rem; text-align: left; margin-top: 0.5rem; }
        #uv-frame {
          position: fixed; top: 0; left: 0; width: 100%; height: 100%;
          border: none; z-index: 10;
        }
      </style>
      <script src=""baremux/index.js""></script>
      <script src=""uv/uv.bundle.js""></script>
      <script src=""uv/uv.config.js""></script>
    </head>
    <body>
      <div class=""container"">
        <h1>Private Browser</h1>
        <p class=""subtitle"">Browse the web privately through this VM. Your IP stays hidden.</p>
        <form id=""uv-form"">
          <input id=""uv-search-engine"" value=""https://duckduckgo.com/?q=%s"" type=""hidden"">
          <input id=""uv-address"" type=""text"" placeholder=""Search or enter a URL..."" autofocus autocomplete=""off"">
          <button type=""submit"">Go</button>
        </form>
        <p id=""uv-error""></p>
        <pre id=""uv-error-code""></pre>
        <p class=""info"">Powered by DeCloud &mdash; decentralized, censorship-resistant infrastructure</p>
      </div>
      <iframe style=""display:none"" id=""uv-frame""></iframe>
      <script src=""register-sw.js""></script>
      <script src=""search.js""></script>
      <script src=""index.js""></script>
    </body>
    </html>
    EOFHTML

  # Set up Nginx as reverse proxy with basic auth
  - htpasswd -bc /etc/nginx/.htpasswd user ${DECLOUD_PASSWORD}
  - |
    cat > /etc/nginx/sites-available/webproxy <<'EOFNGINX'
    server {
        listen 8080;
        server_name _;

        # Cross-Origin Isolation headers - REQUIRED for SharedArrayBuffer
        # The epoxy transport uses SharedArrayBuffer for Wisp communication
        add_header Cross-Origin-Opener-Policy ""same-origin"";
        add_header Cross-Origin-Embedder-Policy ""require-corp"";

        auth_basic ""Private Browser"";
        auth_basic_user_file /etc/nginx/.htpasswd;

        location / {
            proxy_pass http://127.0.0.1:3000;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_http_version 1.1;
        }

        # Wisp WebSocket endpoint - no auth (WS upgrade may not carry credentials)
        location /wisp/ {
            proxy_pass http://127.0.0.1:3000;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection ""upgrade"";
            proxy_set_header Host $host;
            proxy_buffering off;
            proxy_read_timeout 86400s;
            proxy_send_timeout 86400s;
            auth_basic off;
        }

        # UV service worker and proxied content - no auth
        location /uv/ {
            proxy_pass http://127.0.0.1:3000;
            auth_basic off;
        }

        # Epoxy transport - no auth (loaded by service worker)
        location /epoxy/ {
            proxy_pass http://127.0.0.1:3000;
            auth_basic off;
        }

        # Bare mux - no auth (loaded by service worker)
        location /baremux/ {
            proxy_pass http://127.0.0.1:3000;
            auth_basic off;
        }
    }
    EOFNGINX

  - rm -f /etc/nginx/sites-enabled/default
  - ln -sf /etc/nginx/sites-available/webproxy /etc/nginx/sites-enabled/webproxy
  - nginx -t && systemctl restart nginx

  # Create systemd service for the Node.js app
  - |
    cat > /etc/systemd/system/webproxy.service <<'EOF'
    [Unit]
    Description=Web Proxy Browser (Ultraviolet)
    After=network.target

    [Service]
    Type=simple
    WorkingDirectory=/opt/webproxy
    ExecStart=/usr/bin/node /opt/webproxy/src/index.js
    Restart=always
    RestartSec=5
    Environment=NODE_ENV=production
    Environment=PORT=3000

    [Install]
    WantedBy=multi-user.target
    EOF

  - systemctl daemon-reload
  - systemctl enable webproxy.service
  - systemctl start webproxy.service
  - systemctl enable nginx

  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║          Web Proxy Browser - DeCloud Template                ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  Browser: https://${DECLOUD_DOMAIN}:8080                     ║
    ║  Username: user                                               ║
    ║  Password: ${DECLOUD_PASSWORD}                               ║
    ║                                                               ║
    ║  Just open the URL, log in, and start browsing.              ║
    ║  All traffic is routed through this VM.                      ║
    ║                                                               ║
    ║  Services:                                                    ║
    ║    systemctl status webproxy    (Node.js proxy)              ║
    ║    systemctl status nginx       (reverse proxy)              ║
    ║                                                               ║
    ║  Logs:                                                        ║
    ║    journalctl -u webproxy -f                                 ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  Web Proxy Browser is ready!

  Open: https://${DECLOUD_DOMAIN}:8080
  Username: user
  Password: ${DECLOUD_PASSWORD}

  Type any URL or search term to browse privately.
  Your real IP stays hidden — sites see the VM's IP.",

            DefaultEnvironmentVariables = new Dictionary<string, string>(),

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8080,
                    Protocol = "http",
                    Description = "Web Proxy Browser UI",
                    IsPublic = true
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:8080",

            EstimatedCostPerHour = 0.02m, // $0.02/hour — very lightweight

            DefaultBandwidthTier = BandwidthTier.Standard, // 50 Mbps

            Status = TemplateStatus.Published,
            IsFeatured = true,

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Stable Diffusion WebUI (CPU) — lightweight, no GPU required
    // ════════════════════════════════════════════════════════════════════════

    private VmTemplate CreateStableDiffusionCpuTemplate()
    {
        return new VmTemplate
        {
            Name = "Stable Diffusion WebUI Forge (CPU)",
            Slug = "stable-diffusion-cpu",
            Version = "2.0.0",
            Category = "ai-ml",
            Description = "Stable Diffusion WebUI Forge running in CPU-only mode. No GPU required. Generate images from text prompts on any hardware.",
            LongDescription = @"## Features
- Stable Diffusion WebUI Forge (actively maintained AUTOMATIC1111 fork)
- Runs entirely on CPU — no GPU or CUDA needed
- Pre-configured with SD 1.5 base model
- Gradio web interface with authentication
- API access at /docs

## Performance Notes
CPU inference is significantly slower than GPU:
- ~2-5 minutes per 512x512 image at 20 steps
- Best suited for testing, low-volume generation, or API integration
- Lower resolution (512x512) recommended for reasonable generation times

## Getting Started
1. Wait for initial setup to complete (~10-15 minutes for first boot)
2. Access the WebUI at `https://${DECLOUD_DOMAIN}:7860`
3. Log in with user / your generated password
4. Start generating images!

## Requirements
- RAM: 8GB minimum (12GB recommended)
- CPU: 4+ cores
- Storage: 30GB for models and dependencies
- No GPU required",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/lllyasviel/stable-diffusion-webui-forge",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024, // 8 GB
                DiskBytes = 30L * 1024 * 1024 * 1024,   // 30 GB
                RequiresGpu = false
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 12L * 1024 * 1024 * 1024, // 12 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
                RequiresGpu = false
            },

            RequiresGpu = false,
            RequiredCapabilities = new List<string>(),

            Tags = new List<string> { "ai", "stable-diffusion", "image-generation", "cpu", "machine-learning", "no-gpu", "forge" },

            CloudInitTemplate = @"#cloud-config

# Stable Diffusion WebUI Forge (CPU) - Automatic Installation
# DeCloud Template v2.0.0 — Uses Forge (maintained fork), no GPU required

packages:
  - git
  - wget
  - python3
  - python3-pip
  - python3-venv
  - python3-setuptools
  - pkg-config
  - libcairo2-dev
  - libgirepository1.0-dev
  - libgl1
  - libglib2.0-0
  - qemu-guest-agent

runcmd:
  - systemctl enable --now qemu-guest-agent

  # Update package index (no full upgrade — keeps boot fast)
  - apt-get update

  # Create application user
  - useradd -m -s /bin/bash sduser

  # Clone Stable Diffusion WebUI Forge
  - su - sduser -c ""git clone https://github.com/lllyasviel/stable-diffusion-webui-forge.git /home/sduser/stable-diffusion-webui""

  # Pre-create venv and install build tools.
  # Pip build-isolation downloads latest setuptools (>=71) which removed pkg_resources.
  # Pre-installing wheel+setuptools avoids build failures for packages that need them.
  - su - sduser -c ""python3 -m venv /home/sduser/stable-diffusion-webui/venv""
  - su - sduser -c ""/home/sduser/stable-diffusion-webui/venv/bin/pip install wheel setuptools""

  # Download SD 1.5 base model (with retry)
  - su - sduser -c ""mkdir -p /home/sduser/stable-diffusion-webui/models/Stable-diffusion""
  - su - sduser -c ""wget --tries=3 --retry-connrefused --waitretry=5 -O /home/sduser/stable-diffusion-webui/models/Stable-diffusion/v1-5-pruned-emaonly.safetensors https://huggingface.co/runwayml/stable-diffusion-v1-5/resolve/main/v1-5-pruned-emaonly.safetensors""

  # Create systemd service (CPU-only flags)
  - |
    cat > /etc/systemd/system/stable-diffusion.service <<'EOF'
    [Unit]
    Description=Stable Diffusion WebUI Forge (CPU)
    After=network.target

    [Service]
    Type=simple
    User=sduser
    Environment=HOME=/home/sduser
    WorkingDirectory=/home/sduser/stable-diffusion-webui
    ExecStart=/home/sduser/stable-diffusion-webui/webui.sh --listen --port 7860 --api --always-cpu --skip-torch-cuda-test --gradio-auth user:${DECLOUD_PASSWORD}
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
    ║     Stable Diffusion WebUI Forge (CPU) - DeCloud Template    ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  WebUI: https://${DECLOUD_DOMAIN}:7860                       ║
    ║  User:  user / ${DECLOUD_PASSWORD}                           ║
    ║  API:   https://${DECLOUD_DOMAIN}:7860/docs                  ║
    ║                                                               ║
    ║  NOTE: CPU-only mode — image generation takes 2-5 min each.  ║
    ║  Use 512x512 resolution and 20 steps for best speed.         ║
    ║                                                               ║
    ║  Service: systemctl status stable-diffusion                  ║
    ║  Logs:    journalctl -u stable-diffusion -f                  ║
    ║                                                               ║
    ║  Models: /home/sduser/stable-diffusion-webui/models          ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  Stable Diffusion WebUI Forge (CPU) is starting up!

  First-time setup will take 10-15 minutes to install Python dependencies.

  Access the WebUI at: https://${DECLOUD_DOMAIN}:7860
  Username: user / Password: ${DECLOUD_PASSWORD}

  CPU mode: image generation takes ~2-5 min per image at 512x512.

  Check status: systemctl status stable-diffusion
  View logs: journalctl -u stable-diffusion -f",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["COMMANDLINE_ARGS"] = "--listen --port 7860 --api --always-cpu --skip-torch-cuda-test"
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 7860,
                    Protocol = "http",
                    Description = "Stable Diffusion WebUI",
                    IsPublic = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/sdapi/v1/sd-models",
                        TimeoutSeconds = 1200 // CPU: pip install + model load is slow
                    }
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:7860",

            EstimatedCostPerHour = 0.10m, // $0.10/hour for CPU instance

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = false,
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,
            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // AI Chatbot (Ollama + Open WebUI) — Self-hosted ChatGPT alternative
    // ════════════════════════════════════════════════════════════════════════

    private VmTemplate CreateOllamaOpenWebUiTemplate()
    {
        return new VmTemplate
        {
            Name = "AI Chatbot (Ollama + Open WebUI)",
            Slug = "ai-chatbot-ollama",
            Version = "1.0.0",
            Category = "ai-ml",
            Description = "Self-hosted ChatGPT alternative. Run AI models (Llama, Mistral, Gemma) locally with a beautiful chat interface. No data leaves your server.",
            LongDescription = @"## Your Own Private ChatGPT

Deploy a fully self-hosted AI chatbot that runs entirely on your VM. No API keys, no data sharing, no censorship — just you and the AI.

## Features
- **Beautiful Chat UI** — Open WebUI provides a ChatGPT-like experience
- **Multiple Models** — Switch between Llama 3.2, Mistral, Gemma, CodeLlama, and more
- **100% Private** — All inference runs locally, no data leaves the VM
- **GPU Accelerated** — Automatically uses NVIDIA GPU if available, falls back to CPU
- **Model Management** — Pull, delete, and switch models from the web interface
- **Conversation History** — All chats stored locally on the VM
- **File Upload** — Attach documents for RAG-style Q&A
- **Code Generation** — Syntax-highlighted code with copy button
- **Markdown Rendering** — Rich text formatting in responses

## Getting Started
1. Wait for initial setup to complete (~5-10 minutes for model download)
2. Open `https://${DECLOUD_DOMAIN}` in your browser
3. Log in with username `user` and your generated password
4. Select **llama3.2:3b** from the model dropdown
5. Start chatting!

## Pre-installed Model
- **Llama 3.2 3B** — Fast, capable general-purpose model (2GB download)
  - Great for conversation, writing, coding assistance
  - Runs well on both CPU (slower) and GPU (fast)

## Pull More Models via SSH
```bash
# Larger, more capable models (require more RAM)
docker exec ollama ollama pull llama3.2        # 3B default
docker exec ollama ollama pull mistral         # 7B, great all-rounder
docker exec ollama ollama pull gemma2:9b       # 9B, Google's model
docker exec ollama ollama pull codellama       # 7B, optimized for code
docker exec ollama ollama pull llama3.1:70b    # 70B, needs 40GB+ RAM + GPU

# List installed models
docker exec ollama ollama list
```

## GPU vs CPU Performance
| Model | GPU (RTX 3060) | CPU (8-core) |
|-------|---------------|-------------|
| Llama 3.2 3B | ~30 tokens/sec | ~5-8 tokens/sec |
| Mistral 7B | ~20 tokens/sec | ~2-4 tokens/sec |
| Gemma 9B | ~15 tokens/sec | ~1-3 tokens/sec |

## Architecture
```
nginx (:8080) → Basic Auth → Open WebUI (:3000) → Ollama (:11434)
```

## Why DeCloud for AI?
- **Censorship-Free** — No content filters imposed by cloud providers
- **Privacy** — Your prompts and data never leave the VM
- **No API Costs** — Run unlimited queries, no per-token billing
- **Custom Models** — Load any GGUF model, fine-tuned or community
- **Always Available** — No rate limits, no waitlists, no downtime",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/open-webui/open-webui",
            License = "MIT",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,  // 8 GB
                DiskBytes = 30L * 1024 * 1024 * 1024,    // 30 GB
                RequiresGpu = false
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 16L * 1024 * 1024 * 1024,  // 16 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,    // 50 GB
                RequiresGpu = false
            },

            RequiresGpu = false,
            GpuRequirement = "Optional — NVIDIA GPU with CUDA dramatically improves inference speed",

            Tags = new List<string> { "ai", "chatbot", "ollama", "open-webui", "llm", "llama", "mistral", "self-hosted", "private-ai", "chatgpt-alternative" },

            CloudInitTemplate = @"#cloud-config

# AI Chatbot (Ollama + Open WebUI) - Self-hosted ChatGPT Alternative
# DeCloud Template v1.0.0 — GPU auto-detection, Llama 3.2 pre-installed

packages:
  - curl
  - wget
  - nginx
  - apache2-utils
  - qemu-guest-agent

runcmd:
  - systemctl enable --now qemu-guest-agent

  # Install Docker
  - curl -fsSL https://get.docker.com | sh

  # Create persistent data directories
  - mkdir -p /opt/ollama /opt/open-webui

  # ── GPU Detection & NVIDIA Container Toolkit ──
  - |
    GPU_FLAG=""""
    if lspci | grep -i nvidia > /dev/null 2>&1; then
      echo ""NVIDIA GPU detected — installing container toolkit...""
      apt-get update
      apt-get install -y ubuntu-drivers-common
      ubuntu-drivers autoinstall || true

      if [ ! -f /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg ]; then
        curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
          | gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
      fi
      curl -s -L https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
        | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
        | tee /etc/apt/sources.list.d/nvidia-container-toolkit.list
      apt-get update
      apt-get install -y nvidia-container-toolkit
      nvidia-ctk runtime configure --runtime=docker
      systemctl restart docker
      GPU_FLAG=""--gpus=all""
      echo ""GPU_MODE=true"" > /opt/ollama/gpu-status
      echo ""GPU acceleration enabled""
    else
      echo ""No GPU detected — running in CPU-only mode""
      echo ""GPU_MODE=false"" > /opt/ollama/gpu-status
    fi

    # ── Start Ollama container ──
    docker run -d \
      --name ollama \
      --restart unless-stopped \
      $GPU_FLAG \
      -v /opt/ollama:/root/.ollama \
      -p 11434:11434 \
      ollama/ollama:latest

    # Wait for Ollama API to be ready
    echo ""Waiting for Ollama to start...""
    for i in $(seq 1 60); do
      if curl -sf http://localhost:11434/api/tags > /dev/null 2>&1; then
        echo ""Ollama is ready!""
        break
      fi
      sleep 2
    done

    # Pull default model (llama3.2:3b — fast, capable, 2GB)
    echo ""Pulling llama3.2:3b model (this takes 2-5 minutes)...""
    docker exec ollama ollama pull llama3.2:3b

  # ── Start Open WebUI container ──
  - |
    docker run -d \
      --name open-webui \
      --restart unless-stopped \
      --add-host=host.docker.internal:host-gateway \
      -v /opt/open-webui:/app/backend/data \
      -e OLLAMA_BASE_URL=http://host.docker.internal:11434 \
      -e WEBUI_AUTH=false \
      -e ENABLE_SIGNUP=false \
      -e DEFAULT_MODELS=llama3.2:3b \
      -e ENABLE_COMMUNITY_SHARING=false \
      -p 3000:8080 \
      ghcr.io/open-webui/open-webui:main

  # ── Nginx reverse proxy with basic auth ──
  - htpasswd -bc /etc/nginx/.htpasswd user ${DECLOUD_PASSWORD}
  - |
    cat > /etc/nginx/sites-available/ai-chatbot <<'EOFNGINX'
    # Skip basic auth when the request carries a Bearer token (Open WebUI JWT)
    map $http_authorization $auth_type {
        default          ""AI Chatbot"";
        ""~^Bearer ""    off;
    }

    server {
        listen 8080;
        server_name _;

        client_max_body_size 100M;

        auth_basic $auth_type;
        auth_basic_user_file /etc/nginx/.htpasswd;

        # Health endpoint (no auth) — for readiness checks
        location /health {
            proxy_pass http://127.0.0.1:3000/health;
            auth_basic off;
        }

        location / {
            proxy_pass http://127.0.0.1:3000;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection ""upgrade"";
            proxy_buffering off;
            proxy_read_timeout 600s;
            proxy_send_timeout 600s;
        }
    }
    EOFNGINX

  - rm -f /etc/nginx/sites-enabled/default
  - ln -sf /etc/nginx/sites-available/ai-chatbot /etc/nginx/sites-enabled/ai-chatbot
  - nginx -t && systemctl restart nginx
  - systemctl enable nginx

  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║    AI Chatbot (Ollama + Open WebUI) - DeCloud Template       ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  Chat UI:  https://${DECLOUD_DOMAIN}                         ║
    ║  Username: user                                               ║
    ║  Password: ${DECLOUD_PASSWORD}                               ║
    ║                                                               ║
    ║  Default Model: llama3.2:3b                                  ║
    ║                                                               ║
    ║  Pull more models:                                            ║
    ║    docker exec ollama ollama pull mistral                     ║
    ║    docker exec ollama ollama pull codellama                   ║
    ║    docker exec ollama ollama pull gemma2:9b                   ║
    ║                                                               ║
    ║  Services:                                                    ║
    ║    docker ps                  (running containers)            ║
    ║    docker logs -f ollama      (Ollama logs)                   ║
    ║    docker logs -f open-webui  (Open WebUI logs)               ║
    ║                                                               ║
    ║  100% Private — no data leaves this server.                   ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  AI Chatbot is starting up!

  Open WebUI: https://${DECLOUD_DOMAIN}
  Username: user / Password: ${DECLOUD_PASSWORD}

  Default model: llama3.2:3b (pre-pulled on first boot)

  Pull more models via SSH:
    docker exec ollama ollama pull mistral
    docker exec ollama ollama pull codellama

  No data leaves your server. 100% private AI.",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["DEFAULT_MODELS"] = "llama3.2:3b"
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8080,
                    Protocol = "http",
                    Description = "Open WebUI Chat Interface",
                    IsPublic = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/health",
                        TimeoutSeconds = 600 // Docker pull + model download on first boot
                    }
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}",
            DefaultUsername = "user",
            UseGeneratedPassword = true,

            EstimatedCostPerHour = 0.15m, // $0.15/hour — moderate workload

            DefaultBandwidthTier = BandwidthTier.Basic, // AI chat is low bandwidth

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,
            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // vLLM Inference Server — OpenAI-compatible GPU inference API
    // ════════════════════════════════════════════════════════════════════════

    private VmTemplate CreateVllmInferenceServerTemplate()
    {
        return new VmTemplate
        {
            Name = "vLLM Inference Server",
            Slug = "vllm-inference-server",
            Version = "1.0.0",
            Category = "ai-ml",
            Description = "Production-ready LLM inference server with OpenAI-compatible API. Serve Llama, Mistral, Qwen, and other models with GPU acceleration.",
            LongDescription = @"## Production LLM Inference API

Deploy a GPU-accelerated LLM inference server powered by [vLLM](https://docs.vllm.ai/) — the industry standard for high-throughput model serving. Exposes an **OpenAI-compatible API** so any application or SDK that works with OpenAI will work with your server.

## Features
- **OpenAI-compatible API** — Drop-in replacement for `https://api.openai.com/v1`
- **GPU Accelerated** — Optimized CUDA kernels, PagedAttention for efficient VRAM usage
- **Multiple Models** — Serve any HuggingFace model (Llama 3, Mistral, Qwen 2.5, Gemma 2, etc.)
- **Quantization Support** — AWQ/GPTQ quantized models for 8GB GPUs
- **Continuous Batching** — High throughput under concurrent requests
- **Streaming** — Full SSE streaming support for chat completions
- **API Key Auth** — Secured with your generated password as the API key
- **No Data Sharing** — 100% self-hosted, no telemetry

## Getting Started
1. Wait for initial setup to complete (~5-10 minutes for model download)
2. Your API is available at `https://${DECLOUD_DOMAIN}/v1`
3. API key: your generated password (`${DECLOUD_PASSWORD}`)

## Usage Examples

### curl
```bash
curl https://${DECLOUD_DOMAIN}/v1/chat/completions \
  -H ""Authorization: Bearer ${DECLOUD_PASSWORD}"" \
  -H ""Content-Type: application/json"" \
  -d '{
    ""model"": ""Qwen/Qwen2.5-3B-Instruct"",
    ""messages"": [{""role"": ""user"", ""content"": ""Hello!""}]
  }'
```

### Python (OpenAI SDK)
```python
from openai import OpenAI

client = OpenAI(
    base_url=""https://${DECLOUD_DOMAIN}/v1"",
    api_key=""${DECLOUD_PASSWORD}""
)

response = client.chat.completions.create(
    model=""Qwen/Qwen2.5-3B-Instruct"",
    messages=[{""role"": ""user"", ""content"": ""Hello!""}],
    stream=True
)
for chunk in response:
    print(chunk.choices[0].delta.content, end="""")
```

## Default Model
- **Qwen 2.5 3B Instruct** — Fast, capable, fits in 8GB VRAM
  - Good for chat, code, reasoning
  - ~30-40 tokens/sec on RTX 3060/4060

## Switching Models via SSH
```bash
# Edit the model and restart
nano /opt/vllm/model.env    # Change MODEL_ID
systemctl restart vllm

# Popular models for 8GB VRAM (use quantized):
# - Qwen/Qwen2.5-3B-Instruct (default, 3B)
# - meta-llama/Llama-3.2-3B-Instruct (3B)
# - mistralai/Mistral-7B-Instruct-v0.3 (7B, needs quantization)
# - TheBloke/Mistral-7B-Instruct-v0.2-AWQ (7B AWQ, fits 8GB)
```

## Architecture
```
nginx (:8080) → API Key Auth → vLLM OpenAI Server (:8000)
```

## Why vLLM on DeCloud?
- **No API Costs** — Unlimited tokens, pay only for compute
- **Privacy** — Your prompts never leave the VM
- **Low Latency** — Direct GPU inference, no shared infrastructure
- **Full Control** — Any model, any configuration, no content filters",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/vllm-project/vllm",
            License = "Apache-2.0",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 16L * 1024 * 1024 * 1024,  // 16 GB
                DiskBytes = 40L * 1024 * 1024 * 1024,    // 40 GB
                RequiresGpu = true,
                GpuModel = "NVIDIA"
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 32L * 1024 * 1024 * 1024,  // 32 GB
                DiskBytes = 80L * 1024 * 1024 * 1024,    // 80 GB
                RequiresGpu = true,
                GpuModel = "NVIDIA"
            },

            RequiresGpu = true,
            GpuRequirement = "NVIDIA GPU with CUDA support and 8GB+ VRAM (RTX 3060/4060 or better)",
            ContainerImage = "vllm/vllm-openai:latest",
            RequiredCapabilities = new List<string> { "cuda", "nvidia-gpu" },

            Tags = new List<string> { "ai", "llm", "inference", "vllm", "openai-api", "gpu", "llama", "mistral", "qwen", "api-server", "self-hosted" },

            CloudInitTemplate = @"#cloud-config

# vLLM Inference Server — OpenAI-compatible GPU LLM serving
# DeCloud Template v1.0.0 — GPU required, API key auth via nginx

packages:
  - curl
  - wget
  - nginx
  - apache2-utils
  - qemu-guest-agent

runcmd:
  - systemctl enable --now qemu-guest-agent

  # Install Docker
  - curl -fsSL https://get.docker.com | sh

  # Create persistent directories
  - mkdir -p /opt/vllm /opt/hf-cache

  # Store model configuration
  - |
    cat > /opt/vllm/model.env <<'ENVEOF'
    MODEL_ID=Qwen/Qwen2.5-3B-Instruct
    MAX_MODEL_LEN=4096
    GPU_MEMORY_UTILIZATION=0.90
    ENVEOF

  # ── GPU Setup & NVIDIA Container Toolkit ──
  - |
    if ! lspci | grep -i nvidia > /dev/null 2>&1; then
      echo ""ERROR: No NVIDIA GPU detected. vLLM requires a GPU.""
      echo ""GPU_DETECTED=false"" > /opt/vllm/gpu-status
      exit 1
    fi

    echo ""NVIDIA GPU detected — installing container toolkit...""
    apt-get update
    apt-get install -y ubuntu-drivers-common
    ubuntu-drivers autoinstall || true

    if [ ! -f /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg ]; then
      curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
        | gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
    fi
    curl -s -L https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
      | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
      | tee /etc/apt/sources.list.d/nvidia-container-toolkit.list
    apt-get update
    apt-get install -y nvidia-container-toolkit
    nvidia-ctk runtime configure --runtime=docker
    systemctl restart docker
    echo ""GPU_DETECTED=true"" > /opt/vllm/gpu-status
    echo ""GPU acceleration enabled""

  # ── Create vLLM systemd service ──
  - |
    cat > /etc/systemd/system/vllm.service <<'SVCEOF'
    [Unit]
    Description=vLLM Inference Server
    After=docker.service
    Requires=docker.service

    [Service]
    Type=simple
    EnvironmentFile=/opt/vllm/model.env
    ExecStartPre=-/usr/bin/docker rm -f vllm-server
    ExecStart=/usr/bin/docker run --rm \
      --name vllm-server \
      --gpus all \
      --ipc=host \
      -v /opt/hf-cache:/root/.cache/huggingface \
      -p 8000:8000 \
      vllm/vllm-openai:latest \
      --model ${MODEL_ID} \
      --max-model-len ${MAX_MODEL_LEN} \
      --gpu-memory-utilization ${GPU_MEMORY_UTILIZATION} \
      --trust-remote-code
    ExecStop=/usr/bin/docker stop vllm-server
    Restart=on-failure
    RestartSec=10

    [Install]
    WantedBy=multi-user.target
    SVCEOF

    systemctl daemon-reload
    systemctl enable vllm
    systemctl start vllm

  # Wait for vLLM to load model and become ready
  - |
    echo ""Waiting for vLLM to load model (this may take 5-10 minutes)...""
    for i in $(seq 1 180); do
      if curl -sf http://localhost:8000/health > /dev/null 2>&1; then
        echo ""vLLM is ready!""
        break
      fi
      sleep 5
    done

  # ── Nginx reverse proxy with API key auth ──
  - htpasswd -bc /etc/nginx/.htpasswd apikey ${DECLOUD_PASSWORD}
  - |
    cat > /etc/nginx/sites-available/vllm-api <<'EOFNGINX'
    # API key validation: accept Bearer token matching the generated password
    map $http_authorization $api_key_valid {
        default                   0;
        ""~^Bearer ${DECLOUD_PASSWORD}$"" 1;
    }

    server {
        listen 8080;
        server_name _;

        client_max_body_size 10M;

        # Health endpoint (no auth) — for readiness checks
        location /health {
            proxy_pass http://127.0.0.1:8000/health;
        }

        # All other requests require API key
        location / {
            if ($api_key_valid = 0) {
                return 401 '{ ""error"": { ""message"": ""Invalid API key"", ""type"": ""invalid_request_error"" } }';
            }

            proxy_pass http://127.0.0.1:8000;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_http_version 1.1;
            proxy_set_header Connection """";
            proxy_buffering off;
            proxy_read_timeout 600s;
            proxy_send_timeout 600s;
        }
    }
    EOFNGINX

  - rm -f /etc/nginx/sites-enabled/default
  - ln -sf /etc/nginx/sites-available/vllm-api /etc/nginx/sites-enabled/vllm-api
  - nginx -t && systemctl restart nginx
  - systemctl enable nginx

  # Create welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║    vLLM Inference Server - DeCloud Template                  ║
    ╠═══════════════════════════════════════════════════════════════╣
    ║                                                               ║
    ║  API Endpoint:  https://${DECLOUD_DOMAIN}/v1                 ║
    ║  API Key:       ${DECLOUD_PASSWORD}                          ║
    ║  Model:         Qwen/Qwen2.5-3B-Instruct                    ║
    ║                                                               ║
    ║  Test it:                                                     ║
    ║    curl https://${DECLOUD_DOMAIN}/v1/models \                ║
    ║      -H ""Authorization: Bearer ${DECLOUD_PASSWORD}""          ║
    ║                                                               ║
    ║  Switch model:                                                ║
    ║    nano /opt/vllm/model.env                                  ║
    ║    systemctl restart vllm                                     ║
    ║                                                               ║
    ║  Logs:                                                        ║
    ║    journalctl -u vllm -f                                     ║
    ║    docker logs -f vllm-server                                 ║
    ║                                                               ║
    ╚═══════════════════════════════════════════════════════════════╝
    EOF

final_message: |
  vLLM Inference Server is starting up!

  API Endpoint: https://${DECLOUD_DOMAIN}/v1
  API Key: ${DECLOUD_PASSWORD}

  Model: Qwen/Qwen2.5-3B-Instruct (loading on first boot)

  Test:
    curl https://${DECLOUD_DOMAIN}/v1/models -H ""Authorization: Bearer ${DECLOUD_PASSWORD}""

  100% private — your prompts never leave this server.",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["MODEL_ID"] = "Qwen/Qwen2.5-3B-Instruct",
                ["MAX_MODEL_LEN"] = "4096",
                ["GPU_MEMORY_UTILIZATION"] = "0.90"
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8080,
                    Protocol = "http",
                    Description = "vLLM OpenAI-compatible API",
                    IsPublic = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/health",
                        TimeoutSeconds = 900 // Model download + loading can take a while
                    }
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}/v1",
            UseGeneratedPassword = true,

            EstimatedCostPerHour = 0.25m, // GPU workload

            DefaultBandwidthTier = BandwidthTier.Basic, // API traffic is low bandwidth

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,
            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

}