using DeCloud.Orchestrator.Services;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Tenant;

/// <summary>
/// Seeds the <c>platform-general</c> tenant VM template into MongoDB at startup.
/// Mirror of <see cref="SystemVm.SystemVmTemplateSeeder"/> for tenant VMs;
/// kept as a separate class because tenant templates have different shape
/// (no obligation routing, public marketplace listing, user-supplied password).
///
/// <para>
/// <b>Three-step seed:</b>
/// </para>
/// <list type="number">
///   <item>Fetch <c>base-templates/base-tenant.yaml</c> and
///     <c>tenant-vms/general/cloud-init.yaml</c> from DeCloud.Builds at the
///     pinned git ref.</item>
///   <item>Run <see cref="TemplateComposer.Compose"/> to produce a single
///     cloud-init document. Same composer that renders system VM templates;
///     exercises the merge logic in production at startup.</item>
///   <item>Upsert a <see cref="VmTemplate"/> with the composed content,
///     declared <c>Variables</c> for every <c>__VARNAME__</c> placeholder,
///     and two inline artifacts (<c>general-api</c>, <c>general-index</c>).
///     Revision-aware: skips if stored revision ≥ seeder revision.</item>
/// </list>
///
/// <para>
/// <b>Updating the template:</b>
/// </para>
/// <list type="number">
///   <item>Edit <c>base-tenant.yaml</c> or
///     <c>tenant-vms/general/cloud-init.yaml</c> in DeCloud.Builds.</item>
///   <item>If artifacts changed, run <c>compute-artifact-constants.sh</c> in
///     <c>tenant-vms/</c> and copy the updated SHA256 / data: URI constants
///     into this file.</item>
///   <item>Bump <see cref="GeneralTemplateRevision"/>.</item>
///   <item>Commit. On next orchestrator startup, the seeder detects the
///     higher revision and updates MongoDB. Running tenant VMs are not
///     redeployed (revision change drives the next deploy, not running
///     VMs).</item>
/// </list>
/// </summary>
public sealed partial class TenantVmTemplateSeeder
{
    // ── Source pinning ───────────────────────────────────────────────────

    /// <summary>
    /// Git ref (branch, tag, or commit SHA) used to fetch cloud-init YAML and
    /// base-tenant.yaml from DeCloud.Builds at seed time. Update when the YAML
    /// changes and bump <see cref="GeneralTemplateRevision"/>.
    /// </summary>
    private const string CloudInitRef = "main";

    private const string CloudInitRawBase =
        $"https://raw.githubusercontent.com/bekirmfr/DeCloud.Builds/{CloudInitRef}";

    private const string BaseTenantUrl =
        $"{CloudInitRawBase}/base-templates/base-tenant.yaml";

    private const string GeneralRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/general/cloud-init.yaml";


    // ── Template revision ────────────────────────────────────────────────

    /// <summary>
    /// Bumped whenever the composed cloud-init or any artifact changes in a
    /// way that should trigger re-deployment for new tenant VMs created from
    /// this template. Existing running VMs are not affected by revision bumps.
    /// </summary>
    private const int GeneralTemplateRevision = 1;

    // Per-template role layer URLs
    private const string AiChatbotRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/ai-chatbot/cloud-init.yaml";
    private const string MinecraftPaperRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/minecraft-paper/cloud-init.yaml";
    private const string CoolifyRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/coolify/cloud-init.yaml";
    private const string LeaderboardRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/leaderboard/cloud-init.yaml";

    // Per-template revisions — bump when role layer or seeder metadata changes
    // in a way that should redeploy new VMs created from this template.
    private const int AiChatbotTemplateRevision = 1;
    private const int MinecraftPaperTemplateRevision = 1;
    private const int CoolifyTemplateRevision = 1;
    private const int LeaderboardTemplateRevision = 6;

    // ── Inline artifact constants (data: URIs) ───────────────────────────
    // Supplied by the partial class in Services/TemplateConstants/
    // TenantVmTemplateSeeder.Artifacts.cs (auto-generated, do not edit).
    //
    // To regenerate after editing a script in DeCloud.Builds:
    //   cd DeCloud.Builds/tenant-vms
    //   bash compute-artifact-constants.sh
    //   cp TenantVmTemplateSeeder.Artifacts.cs \
    //      ../Orchestrator/src/Orchestrator/Services/TemplateConstants/
    //   Bump GeneralTemplateRevision above.

    // ── Infrastructure ───────────────────────────────────────────────────

    private readonly ITemplateService _templateService;
    private readonly DataStore _dataStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TenantVmTemplateSeeder> _logger;

    public TenantVmTemplateSeeder(
        ITemplateService templateService,
        DataStore dataStore,
        HttpClient httpClient,
        ILogger<TenantVmTemplateSeeder> logger)
    {
        _templateService = templateService;
        _dataStore = dataStore;
        _httpClient = httpClient;
        _logger = logger;
    }

    // ── Entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Seed the <c>platform-general</c> template. Called from
    /// <see cref="TemplateSeederService.SeedAsync"/> after system VM
    /// templates. Idempotent: skips if stored revision ≥ seeder revision;
    /// updates in place if seeder revision is higher.
    /// </summary>
    /// <summary>
    /// Seed all tenant templates. Called from
    /// <see cref="TemplateSeederService.SeedAsync"/> after system VM templates.
    /// Three phases, each its own failure domain (mirrors the former TrySeed
    /// isolation in TemplateSeederService): legacy marketplace templates
    /// (semver, ITemplateService), the general-purpose template (compose +
    /// revision), and the compose-pipeline marketplace templates.
    /// </summary>
    public async Task SeedAsync(bool force = false, CancellationToken ct = default)
    {
        await TryPhase("tenant marketplace (legacy)", () => SeedTemplatesAsync(force));
        await TryPhase("tenant general",
            async () => await SeedTemplateAsync(await BuildGeneralTemplateAsync(ct), ct));
        await TryPhase("compose tenant", () => SeedComposeTenantTemplatesAsync());
    }

    private async Task TryPhase(string label, Func<Task> phase)
    {
        try
        {
            await phase();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tenant seed phase failed: {Label}. Other phases continue. " +
                "Affected templates remain unavailable until the issue is fixed " +
                "and the orchestrator restarts.", label);
        }
    }

    // ── Template builder ─────────────────────────────────────────────────

    private async Task<VmTemplate> BuildGeneralTemplateAsync(CancellationToken ct)
    {
        // Step 1: fetch both layers from DeCloud.Builds.
        var baseLayer = await FetchAsync(BaseTenantUrl, ct);
        var roleLayer = await FetchAsync(GeneralRoleUrl, ct);

        // Step 2: compose. TemplateComposer is the same merger that runs at
        // seed time for system VMs and at render time inside CloudInitRenderer.
        var composed = TemplateComposer.Compose(
            baseLayer, roleLayer,
            baseName: "base-tenant.yaml",
            roleName: "tenant-vms/general/cloud-init.yaml");

        return new VmTemplate
        {
            Slug = "platform-general",
            Name = "General Purpose VM",
            Description =
                "General-purpose tenant VM. " +
                "Suitable for SSH access, custom workloads, and general-purpose computing.",
            Version = "1.0.0",
            Revision = GeneralTemplateRevision,
            Category = "general",
            AuthorId = "system",
            IsCommunity = false,
            IsVerified = true,
            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            PricingModel = TemplatePricingModel.Free,
            CloudInitTemplate = composed,

            Variables = BuildVariables(),
            Artifacts = BuildArtifacts(),
        };
    }

    /// <summary>
    /// Declare every <c>__VARNAME__</c> placeholder in the composed cloud-init
    /// as a Static <see cref="TemplateVariable"/>. The renderer's Pass 1 walks
    /// this list, looks up each via <see cref="IVariableResolverRegistry"/>,
    /// and substitutes. The validator (Pass 3) catches any drift between this
    /// list and the actual placeholders in <c>composed</c>.
    ///
    /// <para>
    /// <b>Source of truth:</b> placeholders found via
    /// <c>grep -hoE '__[A-Z][A-Z0-9_]+__' base-tenant.yaml general/cloud-init.yaml</c>
    /// at the time this seeder was written. If a placeholder is added or
    /// removed in DeCloud.Builds, this list and the resolver registry must be
    /// updated together — the validator will throw at render time otherwise.
    /// </para>
    ///
    /// <para>
    /// All entries are Static. Tenant VMs do not currently use the Dynamic
    /// variable / watcher pattern (that's system-VM only). When tenant VMs
    /// gain dynamic variables, declare them with <c>Kind = Dynamic</c> and a
    /// concrete <see cref="WatcherScope"/>.
    /// </para>
    /// </summary>
    private static List<TemplateVariable> BuildVariables() => new()
    {
        // Identity (resolved from ctx.Vm)
        new() { Name = "VM_ID",       Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID)." },
        new() { Name = "VM_NAME",     Kind = VariableKind.Static, Required = true,
                Description = "VM display name." },
        new() { Name = "HOSTNAME",    Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname for the VM (currently equals VM_NAME)." },

        // Platform context (resolved from ctx.OrchestratorUrl, ctx.Node)
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // SSH / password block (resolved from ctx.Vm.Spec.SshPublicKey, UserSuppliedStatics)
        new() { Name = "CA_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description =
                    "YAML chunk listing user SSH public keys. Empty when neither " +
                    "the VM spec nor user input provided keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description =
                    "YAML chunk for chpasswd.users (cloud-init 22.3+ format). " +
                    "Empty when no admin password is set." },
        new() { Name = "ADMIN_PASSWORD", Kind = VariableKind.Static,
                DefaultValue = "",
                Description =
                    "Plaintext root password. Set via UserSuppliedStatics " +
                    "[\"ADMIN_PASSWORD\"] at deploy time. Empty for SSH-only deploys." },
        new() { Name = "SSH_PASSWORD_AUTH", Kind = VariableKind.Static,
                DefaultValue = "false",
                Description =
                    "'true' or 'false' string for cloud-init's ssh_pwauth. " +
                    "Derived from ADMIN_PASSWORD presence." },
    };

    private static List<TemplateArtifact> BuildArtifacts() => new()
    {
        // ── Inline (data: URI) ───────────────────────────────────────────
        TemplateArtifact.Artifact("general-api", "General-purpose VM dashboard API (Python)",
            ArtifactType.Script,
            sha256: GeneralApiPySha256, sourceUrl: GeneralApiPyDataUri),

        TemplateArtifact.Artifact("general-index", "General-purpose VM dashboard HTML",
            ArtifactType.WebAsset,
            sha256: GeneralIndexHtmlSha256, sourceUrl: GeneralIndexHtmlDataUri),
    };

    // ── Upsert with revision-aware skip ──────────────────────────────────

    private async Task SeedTemplateAsync(VmTemplate template, CancellationToken ct)
    {
        var existing = await _dataStore.GetTemplateBySlugAsync(template.Slug);

        if (existing is not null && existing.Revision >= template.Revision)
        {
            _logger.LogDebug(
                "Tenant template '{Slug}' r{Stored} ≥ seeder r{New} — skipping",
                template.Slug, existing.Revision, template.Revision);
            return;
        }

        if (existing is not null)
        {
            template.Id = existing.Id; // preserve MongoDB _id — update in-place
            _logger.LogInformation(
                "Updating tenant template '{Slug}': r{Old} → r{New}",
                template.Slug, existing.Revision, template.Revision);
        }
        else
        {
            _logger.LogInformation(
                "Seeding new tenant template '{Slug}' r{Rev}",
                template.Slug, template.Revision);
        }

        await _dataStore.SaveTemplateAsync(template);

        _logger.LogInformation(
            "✓ Tenant template '{Slug}' seeded " +
            "(r{Rev}, {VarCount} declared variables, {HttpsCount} HTTPS + {InlineCount} inline artifacts)",
            template.Slug,
            template.Revision,
            template.Variables.Count,
            template.Artifacts.Count(a => !a.IsInline),
            template.Artifacts.Count(a => a.IsInline));
    }

    // ── HTTP fetch ───────────────────────────────────────────────────────

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        _logger.LogDebug("Fetching {Url}", url);
        return await _httpClient.GetStringAsync(url, ct);
    }

    // ════════════════════════════════════════════════════════════════════
    // Legacy marketplace templates (moved verbatim from TemplateSeederService).
    // ${VARNAME} inline cloud-init, semver idempotency via ITemplateService.
    // Migration to the compose/revision path is deferred.
    // ════════════════════════════════════════════════════════════════════

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


    private List<VmTemplate> GetSeedTemplates()
    {
        return new List<VmTemplate>
        {
            CreateStableDiffusionTemplate(),
            CreateStableDiffusionCpuTemplate(),
            CreateFluxTemplate(),
            CreatePostgreSqlTemplate(),
            CreateCodeServerTemplate(),
            CreatePrivateBrowserTemplate(),
            CreateShadowsocksProxyTemplate(),
            CreateWebProxyBrowserTemplate(),
            // AI Chatbot (Ollama + Open WebUI) migrated to the compose pipeline.
            // See SeedComposeTenantTemplatesAsync() and BuildAiChatbotTemplateAsync()
            // at the bottom of this file. Composing base-tenant.yaml +
            // tenant-vms/ai-chatbot/cloud-init.yaml inherits SSH password auth,
            // qemu-guest-agent, sshd drop-in, and chpasswd bootcmd from
            // base-tenant.yaml instead of bypassing them.
            CreateVllmInferenceServerTemplate(),
            CreatePytorchJupyterTemplate()
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
- RAM: 10GB minimum
- Storage: 40GB for models and cache",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/lllyasviel/stable-diffusion-webui-forge",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 10L * 1024 * 1024 * 1024, // 10 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
                GpuMode = GpuMode.Proxied,
                GpuModel = "NVIDIA"
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 32L * 1024 * 1024 * 1024, // 32 GB
                DiskBytes = 100L * 1024 * 1024 * 1024,  // 100 GB
                GpuMode = GpuMode.Passthrough,
                GpuModel = "NVIDIA RTX 3090"
            },

            RequiresGpu = true,
            DefaultGpuMode = GpuMode.Proxied,
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

  # GPU access via DeCloud GPU proxy shim (injected by EnsureGpuProxyShim)
  # Create application user
  - useradd -m -s /bin/bash sduser

  # Clone Stable Diffusion WebUI Forge
  - su - sduser -c ""git clone https://github.com/lllyasviel/stable-diffusion-webui-forge.git /home/sduser/stable-diffusion-webui""

  # Pre-create venv and install build tools.
  # Pip build-isolation downloads latest setuptools (>=71) which removed pkg_resources.
  # Pre-installing wheel+setuptools avoids build failures for packages that need them.
  - su - sduser -c ""python3 -m venv /home/sduser/stable-diffusion-webui/venv""
  - su - sduser -c ""/home/sduser/stable-diffusion-webui/venv/bin/pip install joblib""

  # Disable cuDNN globally — Bug 19 parked.
  - printf 'try:\n    import torch\n    _orig_lazy_init = torch.cuda._lazy_init\n    def _patched_lazy_init(_orig=_orig_lazy_init):\n        _orig()\n        torch.backends.cudnn.enabled = False\n    torch.cuda._lazy_init = _patched_lazy_init\nexcept ImportError:\n    pass\n' > /home/sduser/stable-diffusion-webui/venv/lib/python3.10/site-packages/decloud_cudnn_disable.py
  - echo ""import decloud_cudnn_disable"" > /home/sduser/stable-diffusion-webui/venv/lib/python3.10/site-packages/decloud_cudnn_disable.pth

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
    PermissionsStartOnly=true
    EnvironmentFile=-/etc/decloud/gpu-proxy.env
    Environment=HOME=/home/sduser
    WorkingDirectory=/home/sduser/stable-diffusion-webui
    ExecStartPre=/bin/bash -c 'find /home/sduser/stable-diffusion-webui/venv -name ""libcublas.so.12"" | xargs -I{} cp /usr/local/lib/libcublas_stub.so {} && find /home/sduser/stable-diffusion-webui/venv -name ""libcublasLt.so.12"" | xargs -I{} cp /usr/local/lib/libcublasLt_stub.so {} && find /home/sduser/stable-diffusion-webui/venv -name ""libcudnn*.so.8"" | xargs -I{} cp /usr/local/lib/libcudnn.so.8 {} && SP=$(find /home/sduser/stable-diffusion-webui/venv -path ""*/site-packages"" -maxdepth 5 | head -1) && printf ""try:\n    import torch\n    _orig_lazy_init = torch.cuda._lazy_init\n    def _patched_lazy_init(_orig=_orig_lazy_init):\n        _orig()\n        torch.backends.cudnn.enabled = False\n    torch.cuda._lazy_init = _patched_lazy_init\nexcept ImportError:\n    pass\n"" > $SP/decloud_cudnn_disable.py && echo import decloud_cudnn_disable > $SP/decloud_cudnn_disable.pth'
    ExecStart=/home/sduser/stable-diffusion-webui/webui.sh --listen --port 7860 --api --skip-torch-cuda-test --gradio-auth user:${DECLOUD_PASSWORD}
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
                ["COMMANDLINE_ARGS"] = "--listen --port 7860 --api --no-clean-temp-dir",
                // GPU proxy: stable diffusion works well with graph noops (uses custom kernels)
                ["DECLOUD_GPU_GRAPH_NOOP"] = "1",
                // VMM: required for PyTorch memory allocator used by Forge
                ["DECLOUD_GPU_VMEM_PROXY"] = "1",
                // Required for PyTorch CUDA 12 lazy loading — without this,
                // __cudaRegisterFunction is bypassed and kernel launches fail silently
                ["CUDA_MODULE_LOADING"] = "EAGER",
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

    private VmTemplate CreateFluxTemplate()
    {
        return new VmTemplate
        {
            Name = "FLUX.1 Image Generation",
            Slug = "flux-image-generation",
            Version = "1.1.0",
            Category = "ai-ml",
            Description = "FLUX.1-dev (NF4 quantized) via SD WebUI Forge. Best-in-class open-source image generation, unrestricted, on 8GB+ VRAM.",
            LongDescription = @"## Features
- FLUX.1-dev — best-in-class open-source image generation model
- NF4 quantized checkpoint — fits in 8GB VRAM
- SD WebUI Forge interface (actively maintained)
- Pre-downloaded: checkpoint, VAE, CLIP-L & T5-XXL fp8 text encoders
- Unrestricted generation — no content filters
- Auto-tunes memory flags based on available VRAM and RAM

## Getting Started
1. Wait for first-boot setup to complete (~15-20 minutes, downloading ~15GB of models)
2. Access the WebUI at `https://${DECLOUD_DOMAIN}:7860`
3. Login with `user` / `${DECLOUD_PASSWORD}`
4. Select `flux1-dev-bnb-nf4` from the model dropdown
5. Start generating!

## Requirements
- GPU: NVIDIA with 8GB+ VRAM
- RAM: 8GB minimum (swapfile created automatically if RAM < 20GB)
- Storage: 60GB for models and dependencies",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/lllyasviel/stable-diffusion-webui-forge",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 8L * 1024 * 1024 * 1024,   // 8GB — swapfile compensates for mmap
                DiskBytes = 60L * 1024 * 1024 * 1024,    // 60GB — FLUX files are big
                GpuMode = GpuMode.Proxied,
                GpuModel = "NVIDIA"
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 32L * 1024 * 1024 * 1024,  // 32GB — no swap needed, full speed
                DiskBytes = 100L * 1024 * 1024 * 1024,
                GpuMode = GpuMode.Passthrough,
                GpuModel = "NVIDIA RTX 4070"
            },

            RequiresGpu = true,
            DefaultGpuMode = GpuMode.Proxied,
            GpuRequirement = "NVIDIA GPU with 8GB+ VRAM",
            RequiredCapabilities = new List<string> { "cuda", "nvidia-gpu" },

            Tags = new List<string> { "ai", "flux", "image-generation", "gpu", "unrestricted", "forge" },

            CloudInitTemplate = @"#cloud-config

# FLUX.1-dev (NF4) via SD WebUI Forge — DeCloud Template v1.1.0

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
  - apt-get update
  - useradd -m -s /bin/bash sduser

  # Headless xdg-open stub — Forge tries to open output folder in a file manager
  - printf '#!/bin/bash\nexit 0\n' > /usr/local/bin/xdg-open
  - chmod +x /usr/local/bin/xdg-open

  # Swapfile — only if RAM < 20GB (needed for mmap of 11GB model file)
  - |
    TOTAL_RAM_GB=$(awk '/MemTotal/ {printf ""%d"", $2/1024/1024}' /proc/meminfo)
    if [ ""$TOTAL_RAM_GB"" -lt 20 ]; then
      echo ""RAM ${TOTAL_RAM_GB}GB < 20GB — creating 16GB swapfile""
      fallocate -l 16G /swapfile
      chmod 600 /swapfile
      mkswap /swapfile
      swapon /swapfile
      echo '/swapfile none swap sw 0 0' >> /etc/fstab
    else
      echo ""RAM ${TOTAL_RAM_GB}GB sufficient — skipping swapfile""
    fi

  - su - sduser -c ""git clone https://github.com/lllyasviel/stable-diffusion-webui-forge.git /home/sduser/stable-diffusion-webui""
  - su - sduser -c ""python3 -m venv /home/sduser/stable-diffusion-webui/venv""
  - su - sduser -c ""/home/sduser/stable-diffusion-webui/venv/bin/pip install wheel setuptools joblib""

  # FLUX.1-dev NF4 checkpoint — Civitai mirror (no HuggingFace auth required)
  # tmp+rename pattern prevents Forge from seeing partial/failed downloads
  - su - sduser -c ""mkdir -p /home/sduser/stable-diffusion-webui/models/Stable-diffusion""
  - su - sduser -c ""wget --tries=3 --waitretry=5 -O /home/sduser/stable-diffusion-webui/models/Stable-diffusion/flux1-dev-bnb-nf4.safetensors.tmp 'https://civitai.com/api/download/models/738898?type=Model&format=SafeTensor' && mv /home/sduser/stable-diffusion-webui/models/Stable-diffusion/flux1-dev-bnb-nf4.safetensors.tmp /home/sduser/stable-diffusion-webui/models/Stable-diffusion/flux1-dev-bnb-nf4.safetensors""

  # FLUX VAE
  - su - sduser -c ""mkdir -p /home/sduser/stable-diffusion-webui/models/VAE""
  - su - sduser -c ""wget --tries=3 --waitretry=5 -O /home/sduser/stable-diffusion-webui/models/VAE/ae.safetensors.tmp https://huggingface.co/black-forest-labs/FLUX.1-dev/resolve/main/ae.safetensors && mv /home/sduser/stable-diffusion-webui/models/VAE/ae.safetensors.tmp /home/sduser/stable-diffusion-webui/models/VAE/ae.safetensors""

  # Text encoders
  - su - sduser -c ""mkdir -p /home/sduser/stable-diffusion-webui/models/text_encoder /home/sduser/stable-diffusion-webui/models/text_encoder_2""
  - su - sduser -c ""wget --tries=3 --waitretry=5 -O /home/sduser/stable-diffusion-webui/models/text_encoder/clip_l.safetensors.tmp https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/clip_l.safetensors && mv /home/sduser/stable-diffusion-webui/models/text_encoder/clip_l.safetensors.tmp /home/sduser/stable-diffusion-webui/models/text_encoder/clip_l.safetensors""
  - su - sduser -c ""wget --tries=3 --waitretry=5 -O /home/sduser/stable-diffusion-webui/models/text_encoder_2/t5xxl_fp8_e4m3fn.safetensors.tmp https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/t5xxl_fp8_e4m3fn.safetensors && mv /home/sduser/stable-diffusion-webui/models/text_encoder_2/t5xxl_fp8_e4m3fn.safetensors.tmp /home/sduser/stable-diffusion-webui/models/text_encoder_2/t5xxl_fp8_e4m3fn.safetensors""

  # Write service file with __EXTRA_FLAGS__ placeholder
  - |
    cat > /etc/systemd/system/stable-diffusion.service <<'EOF'
    [Unit]
    Description=FLUX.1 WebUI Forge
    After=network.target

    [Service]
    Type=simple
    User=sduser
    PermissionsStartOnly=true
    EnvironmentFile=-/etc/decloud/gpu-proxy.env
    Environment=HOME=/home/sduser
    WorkingDirectory=/home/sduser/stable-diffusion-webui
    ExecStartPre=/bin/bash -c 'find /home/sduser/stable-diffusion-webui/venv -name ""libcublas.so.12"" | xargs -I{} cp /usr/local/lib/libcublas_stub.so {} && find /home/sduser/stable-diffusion-webui/venv -name ""libcublasLt.so.12"" | xargs -I{} cp /usr/local/lib/libcublasLt_stub.so {} && SP=$(find /home/sduser/stable-diffusion-webui/venv -path ""*/site-packages"" -maxdepth 5 | head -1) && printf ""try:\n    import torch\n    _orig_lazy_init = torch.cuda._lazy_init\n    def _patched_lazy_init(_orig=_orig_lazy_init):\n        _orig()\n        torch.backends.cudnn.enabled = False\n    torch.cuda._lazy_init = _patched_lazy_init\nexcept ImportError:\n    pass\n"" > $SP/decloud_cudnn_disable.py && echo import decloud_cudnn_disable > $SP/decloud_cudnn_disable.pth'
    ExecStart=/home/sduser/stable-diffusion-webui/webui.sh --listen --port 7860 --api --skip-torch-cuda-test --gradio-auth user:${DECLOUD_PASSWORD} __EXTRA_FLAGS__
    Restart=always
    RestartSec=10

    [Install]
    WantedBy=multi-user.target
    EOF

  # Detect VRAM and patch __EXTRA_FLAGS__ placeholder
  # < 12GB VRAM: offload VAE to CPU + aggressive layer offloading
  # >= 12GB VRAM: no flags needed — Forge manages memory automatically
  - |
    VRAM_MB=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1 || echo 0)
    VRAM_GB=$((VRAM_MB / 1024))
    if [ ""$VRAM_GB"" -lt 12 ]; then
      echo ""VRAM ${VRAM_GB}GB < 12GB — enabling vae-in-cpu + always-offload-from-vram""
      EXTRA_FLAGS=""--vae-in-cpu --always-offload-from-vram""
    else
      echo ""VRAM ${VRAM_GB}GB sufficient — no offload flags needed""
      EXTRA_FLAGS=""""
    fi
    sed -i ""s|__EXTRA_FLAGS__|${EXTRA_FLAGS}|g"" /etc/systemd/system/stable-diffusion.service

  - systemctl daemon-reload
  - systemctl enable stable-diffusion.service
  - systemctl start stable-diffusion.service

  # Welcome message
  - |
    cat > /etc/motd <<'EOF'
    ╔═══════════════════════════════════════════════════════════════╗
    ║        FLUX.1-dev Image Generation - DeCloud Template        ║
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
  FLUX.1-dev is starting up. First boot takes 15-20 minutes (model downloads ~15GB total).
  Access: https://${DECLOUD_DOMAIN}:7860 — user / ${DECLOUD_PASSWORD}",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["DECLOUD_GPU_GRAPH_NOOP"] = "1",
                ["DECLOUD_GPU_VMEM_PROXY"] = "1",
                ["CUDA_MODULE_LOADING"] = "EAGER",
            },

            ExposedPorts = new List<TemplatePort>
          {
              new TemplatePort
              {
                  Port = 7860,
                  Protocol = "http",
                  Description = "FLUX WebUI",
                  IsPublic = true,
                  ReadinessCheck = new ServiceCheck
                  {
                      Strategy = CheckStrategy.HttpGet,
                      HttpPath = "/sdapi/v1/sd-models",
                      TimeoutSeconds = 1800 // 30 min — large model downloads
                  }
              }
          },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}:7860",

            EstimatedCostPerHour = 0.75m,

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
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,  // 8 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
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
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,  // 8 GB
                DiskBytes = 50L * 1024 * 1024 * 1024,   // 50 GB
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
                MemoryBytes = 1024L * 1024 * 1024,
                DiskBytes = 10L * 1024 * 1024 * 1024
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 4L * 1024 * 1024 * 1024,  // 4 GB
                DiskBytes = 20L * 1024 * 1024 * 1024,   // 20 GB
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
                QualityTier = QualityTier.Burstable,
                Constraints = new List<Constraint>
                {
                    new()
                    {
                        Target   = ConstraintTargets.Node.Network.HasPublicIp,
                        Operator = ConstraintOperators.Eq,
                        Value    = true
                    }
                }
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
                DiskBytes = 30L * 1024 * 1024 * 1024    // 30 GB
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 12L * 1024 * 1024 * 1024, // 12 GB
                DiskBytes = 50L * 1024 * 1024 * 1024    // 50 GB
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
    // vLLM Inference Server — OpenAI-compatible GPU inference API
    // ════════════════════════════════════════════════════════════════════════

    private VmTemplate CreateVllmInferenceServerTemplate()
    {
        return new VmTemplate
        {
            Name = "vLLM Inference Server",
            Slug = "vllm-inference-server",
            Version = "2.0.0",
            Category = "ai-ml",
            Description = "Production-ready LLM inference server with OpenAI-compatible API. Serve Llama, Mistral, Qwen, and other models with GPU acceleration.",
            LongDescription = @"## Production LLM Inference API

Deploy a GPU-accelerated LLM inference server powered by [vLLM](https://docs.vllm.ai/) — the industry standard for high-throughput model serving. Exposes an **OpenAI-compatible API** so any application or SDK that works with OpenAI will work with your server.

## Who is this for?
This template is designed for developers and teams who need a self-hosted
LLM API endpoint. If you want a chat interface instead, use the
**Ollama + Open WebUI** template.

## Features
- **OpenAI-compatible API** — Drop-in replacement for `https://api.openai.com/v1`
- **GPU Accelerated** — PagedAttention for efficient VRAM usage via DeCloud GPU proxy
- **Multiple Models** — Serve any HuggingFace model (Llama 3, Mistral, Qwen 2.5, Gemma 2, etc.)
- **Quantization Support** — AWQ/GPTQ/bitsandbytes quantized models for 8GB GPUs
- **Continuous Batching** — High throughput under concurrent requests
- **Streaming** — Full SSE streaming support for chat completions
- **API Key Auth** — Secured with your generated password as the API key
- **No Data Sharing** — 100% self-hosted, no telemetry, no Docker required

## Getting Started
1. Wait for initial setup to complete (~10-15 minutes for pip install + model download)
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
    ""messages"": [{""role"": ""user"", ""content"": ""Hello!""}],
    ""stream"": true
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

# Popular models for 8GB VRAM:
# - Qwen/Qwen2.5-3B-Instruct          (default, 3B)
# - meta-llama/Llama-3.2-3B-Instruct  (3B, needs HF token)
# - TheBloke/Mistral-7B-v0.1-AWQ      (7B AWQ, fits 8GB)
# - Qwen/Qwen2.5-7B-Instruct-AWQ      (7B AWQ, fits 8GB)
```

## Architecture
```
nginx (:8080) → Bearer token auth → vLLM OpenAI Server (:8000)
```

## Why vLLM on DeCloud?
- **No API Costs** — Unlimited tokens, pay only for compute
- **Privacy** — Your prompts never leave the VM
- **Uncensored** — No content policy enforcement at the API level
- **Full Control** — Any model, any configuration",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/vllm-project/vllm",
            License = "Apache-2.0",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 16L * 1024 * 1024 * 1024,  // 16GB — vLLM pip deps are heavy
                DiskBytes = 40L * 1024 * 1024 * 1024,    // 40GB — model + venv
                GpuMode = GpuMode.Proxied,
                GpuModel = "NVIDIA"
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 32L * 1024 * 1024 * 1024,
                DiskBytes = 80L * 1024 * 1024 * 1024,
                GpuMode = GpuMode.Proxied,
                GpuModel = "NVIDIA RTX 4070"
            },

            RequiresGpu = true,
            DefaultGpuMode = GpuMode.Proxied,
            GpuRequirement = "NVIDIA GPU with CUDA support and 8GB+ VRAM",
            RequiredCapabilities = new List<string> { "cuda", "nvidia-gpu" },

            Tags = new List<string> { "ai", "llm", "inference", "vllm", "openai-api", "gpu",
                                      "llama", "mistral", "qwen", "api-server", "self-hosted" },

            CloudInitTemplate = @"#cloud-config

# vLLM Inference Server — OpenAI-compatible GPU LLM serving
# DeCloud Template v2.0.0 — pip-based, GPU proxy mode, no Docker

packages:
  - curl
  - wget
  - git
  - python3
  - python3-pip
  - python3-venv
  - nginx
  - apache2-utils
  - qemu-guest-agent

write_files:
  # Model configuration — edit and restart vllm to switch models
  - path: /opt/vllm/model.env
    permissions: '0644'
    content: |
      MODEL_ID=Qwen/Qwen2.5-3B-Instruct
      MAX_MODEL_LEN=8192
      GPU_MEMORY_UTILIZATION=0.90

  # vLLM systemd service
  - path: /etc/systemd/system/vllm.service
    permissions: '0644'
    content: |
      [Unit]
      Description=vLLM Inference Server
      After=network.target

      [Service]
      Type=simple
      EnvironmentFile=/etc/decloud/gpu-proxy.env
      EnvironmentFile=/opt/vllm/model.env
      Environment=HF_HOME=/opt/hf-cache
      Environment=TRANSFORMERS_CACHE=/opt/hf-cache
      Environment=CUDA_MODULE_LOADING=EAGER
      WorkingDirectory=/opt/vllm
      ExecStart=/opt/vllm/venv/bin/python3 -m vllm.entrypoints.openai.api_server \
        --model ${MODEL_ID} \
        --host 127.0.0.1 \
        --port 8000 \
        --max-model-len ${MAX_MODEL_LEN} \
        --gpu-memory-utilization ${GPU_MEMORY_UTILIZATION} \
        --trust-remote-code \
        --disable-log-requests
      Restart=on-failure
      RestartSec=10

      [Install]
      WantedBy=multi-user.target

  # nginx reverse proxy with Bearer token auth
  - path: /etc/nginx/sites-available/vllm-api
    permissions: '0644'
    content: |
      map $http_authorization $api_key_valid {
          default                              0;
          ""~^Bearer ${DECLOUD_PASSWORD}$""  1;
      }

      server {
          listen 8080;
          server_name _;
          client_max_body_size 10M;

          # Health endpoint — no auth (readiness probe)
          location /health {
              proxy_pass http://127.0.0.1:8000/health;
          }

          # All other requests require Bearer token
          location / {
              if ($api_key_valid = 0) {
                  return 401 '{""error"":{""message"":""Invalid API key"",""type"":""invalid_request_error""}}';
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

  # MOTD
  - path: /etc/motd
    permissions: '0644'
    content: |
      ╔═══════════════════════════════════════════════════════════════╗
      ║         vLLM Inference Server — DeCloud Template             ║
      ╠═══════════════════════════════════════════════════════════════╣
      ║                                                               ║
      ║  API Endpoint:  https://${DECLOUD_DOMAIN}/v1                 ║
      ║  API Key:       ${DECLOUD_PASSWORD}                          ║
      ║  Model:         Qwen/Qwen2.5-3B-Instruct                    ║
      ║                                                               ║
      ║  Test:                                                        ║
      ║    curl https://${DECLOUD_DOMAIN}/v1/models \                ║
      ║      -H ""Authorization: Bearer ${DECLOUD_PASSWORD}""         ║
      ║                                                               ║
      ║  Switch model:                                                ║
      ║    nano /opt/vllm/model.env                                  ║
      ║    systemctl restart vllm                                     ║
      ║                                                               ║
      ║  Logs:  journalctl -u vllm -f                                ║
      ║                                                               ║
      ╚═══════════════════════════════════════════════════════════════╝

runcmd:
  - systemctl enable --now qemu-guest-agent
  - apt-get update
  - mkdir -p /opt/vllm /opt/hf-cache

  # Create venv and install vLLM with CUDA 12.1 wheels
  - python3 -m venv /opt/vllm/venv
  - /opt/vllm/venv/bin/pip install --upgrade pip --quiet
  - /opt/vllm/venv/bin/pip install torch==2.3.1+cu121 --index-url https://download.pytorch.org/whl/cu121 --quiet
  - /opt/vllm/venv/bin/pip install vllm==0.6.3 --quiet

  # Disable cuDNN — Bug 19 (export table context struct unknown)
  - |
    SP=$(find /opt/vllm/venv -path ""*/site-packages"" -maxdepth 5 | head -1)
    printf 'try:\n    import torch\n    _orig_lazy_init = torch.cuda._lazy_init\n    def _patched_lazy_init(_orig=_orig_lazy_init):\n        _orig()\n        torch.backends.cudnn.enabled = False\n    torch.cuda._lazy_init = _patched_lazy_init\nexcept ImportError:\n    pass\n' > $SP/decloud_cudnn_disable.py
    echo ""import decloud_cudnn_disable"" > $SP/decloud_cudnn_disable.pth

  # nginx
  - rm -f /etc/nginx/sites-enabled/default
  - ln -sf /etc/nginx/sites-available/vllm-api /etc/nginx/sites-enabled/vllm-api
  - nginx -t && systemctl restart nginx && systemctl enable nginx

  # Start vLLM (model downloads on first start)
  - systemctl daemon-reload
  - systemctl enable vllm
  - systemctl start vllm

final_message: |
  vLLM Inference Server is starting up!
  Model download + loading takes 10-15 minutes on first boot.

  API: https://${DECLOUD_DOMAIN}/v1
  Key: ${DECLOUD_PASSWORD}

  Test: curl https://${DECLOUD_DOMAIN}/v1/models -H ""Authorization: Bearer ${DECLOUD_PASSWORD}""
  Logs: journalctl -u vllm -f",

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["MODEL_ID"] = "Qwen/Qwen2.5-3B-Instruct",
                ["MAX_MODEL_LEN"] = "8192",
                ["GPU_MEMORY_UTILIZATION"] = "0.90",
                // VMM: vLLM uses cuMemCreate/cuMemMap for KV cache allocation
                ["DECLOUD_GPU_VMEM_PROXY"] = "1",
                // Graphs: proxy runs kernels eagerly — CUDA graph capture not supported
                ["DECLOUD_GPU_GRAPH_NOOP"] = "1",
                // Required for PyTorch CUDA 12 lazy loading
                ["CUDA_MODULE_LOADING"] = "EAGER",
                // Disable torch.compile (requires kernel driver, not available in proxy mode)
                ["TORCHINDUCTOR_DISABLE"] = "1",
                // Suppress device-side assertion crashes over proxy
                ["TORCH_USE_CUDA_DSA"] = "0",
                // Tune allocator: avoid repeated VMM RPC round-trips
                ["PYTORCH_CUDA_ALLOC_CONF"] = "max_split_size_mb:128,expandable_segments:False",
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
                      TimeoutSeconds = 1800 // pip install + model download on first boot
                  }
              }
          },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}/v1",
            UseGeneratedPassword = true,

            EstimatedCostPerHour = 0.25m,

            DefaultBandwidthTier = BandwidthTier.Basic,

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

    private VmTemplate CreatePytorchJupyterTemplate()
    {
        return new VmTemplate
        {
            Name = "PyTorch + JupyterLab",
            Slug = "pytorch-jupyter",
            Version = "1.0.0",
            Category = "ai-ml",
            Description = "GPU-accelerated PyTorch 2.x with JupyterLab. Run training, fine-tuning, and inference notebooks with full CUDA support via DeCloud GPU proxy.",
            LongDescription = @"
- **PyTorch 2.x** with CUDA 12.1 — GPU acceleration via DeCloud proxy (no IOMMU needed)
- **JupyterLab** — browser-based notebook IDE, password-protected
- **ML libraries** — transformers, accelerate, datasets, numpy, pandas, matplotlib, scikit-learn
- **bitsandbytes** — 4-bit / 8-bit quantization for LLM fine-tuning
- **Note:** torch.compile() disabled — requires kernel driver not available in proxy mode",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://jupyter.org",
            License = "BSD-3-Clause",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,   // 8 GB RAM
                DiskBytes = 40L * 1024 * 1024 * 1024,    // 40 GB disk (PyTorch ~5 GB)
                GpuMode = GpuMode.Proxied
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 16L * 1024 * 1024 * 1024,  // 16 GB RAM
                DiskBytes = 80L * 1024 * 1024 * 1024,    // 80 GB (datasets + checkpoints)
                GpuMode = GpuMode.Proxied
            },

            RequiresGpu = true,
            DefaultGpuMode = GpuMode.Proxied,
            GpuRequirement = "NVIDIA GPU with 6GB+ VRAM (RTX 3060 or better recommended)",
            Tags = new List<string> { "ai", "pytorch", "jupyter", "python", "ml", "training",
                                      "fine-tuning", "data-science", "gpu", "transformers" },

            CloudInitTemplate = PYTORCH_CLOUD_INIT,  // see below

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                // VMM: PyTorch's caching allocator uses cuMemCreate/cuMemMap/cuMemAddressReserve
                ["DECLOUD_GPU_VMEM_PROXY"] = "1",
                // Graphs: proxy runs kernels eagerly — CUDA graph capture is meaningless
                ["DECLOUD_GPU_GRAPH_NOOP"] = "1",
                // Disable torch.compile/inductor (needs kernel driver, not available in VM)
                ["TORCHINDUCTOR_DISABLE"] = "1",
                // Device-side assertions crash via proxy on CUDA errors — disable
                ["TORCH_USE_CUDA_DSA"] = "0",
                // Tune caching allocator: expandable_segments=False avoids repeated VMM
                // calls that each incur an RPC round-trip
                ["PYTORCH_CUDA_ALLOC_CONF"] = "max_split_size_mb:128,expandable_segments:False",
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 8080,
                    Protocol = "http",
                    Description = "JupyterLab",
                    IsPublic = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/health",
                        TimeoutSeconds = 600  // PyTorch pip install ~5 min on first boot
                    }
                }
            },

            DefaultAccessUrl = "https://${DECLOUD_DOMAIN}",
            DefaultUsername = "admin",
            UseGeneratedPassword = true,
            EstimatedCostPerHour = 0.20m,
            DefaultBandwidthTier = BandwidthTier.Basic,
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

    private const string PYTORCH_CLOUD_INIT = @"#cloud-config

# PyTorch + JupyterLab — DeCloud GPU Template v1.0.0
#
# IMPORTANT: NO heredocs (<<EOF) anywhere in runcmd.
# Heredoc content sits at column 0 in the shell script, which breaks the YAML
# parser — cloud-init sees an invalid document, treats it as empty config, and
# silently skips bootcmd/chpasswd/runcmd entirely. All static file content is
# written via write_files (properly indented = valid YAML). The one dynamic
# file that needs a runtime value (jupyter_lab_config.py) uses echo commands.

packages:
  - curl
  - wget
  - git
  - build-essential
  - python3
  - python3-pip
  - python3-venv
  - nginx
  - apache2-utils
  - qemu-guest-agent

write_files:
  # Welcome notebook — written before runcmd installs PyTorch so the file
  # exists immediately on boot even if pip takes 10+ minutes.
  - path: /opt/notebooks/welcome.ipynb
    permissions: '0644'
    content: |
      {""cells"":[
        {""cell_type"":""markdown"",""metadata"":{},""source"":[""# DeCloud PyTorch GPU\n\nRun the cell below to verify GPU access.""]},
        {""cell_type"":""code"",""metadata"":{},""source"":[
          ""import torch\n"",
          ""print(f'PyTorch {torch.__version__}')\n"",
          ""print(f'CUDA available: {torch.cuda.is_available()}')\n"",
          ""if torch.cuda.is_available():\n"",
          ""    print(f'GPU: {torch.cuda.get_device_name(0)}')\n"",
          ""    x = torch.ones(512, 512, device='cuda')\n"",
          ""    print(f'Tensor sum: {x.sum().item()} — GPU proxy working!')""],""outputs"":[],""execution_count"":null}
      ],""metadata"":{""kernelspec"":{""display_name"":""Python 3"",""language"":""python"",""name"":""python3""}},""nbformat"":4,""nbformat_minor"":5}

  # JupyterLab systemd service
  - path: /etc/systemd/system/jupyterlab.service
    permissions: '0644'
    content: |
      [Unit]
      Description=JupyterLab
      After=network.target

      [Service]
      Type=simple
      User=root
      WorkingDirectory=/opt/notebooks
      EnvironmentFile=-/etc/decloud/gpu-proxy.env
      Environment=TORCHINDUCTOR_DISABLE=1
      Environment=TORCH_USE_CUDA_DSA=0
      Environment=PYTORCH_CUDA_ALLOC_CONF=max_split_size_mb:128,expandable_segments:False
      ExecStart=/opt/jupyter/venv/bin/jupyter lab \
          --config=/root/.jupyter/jupyter_lab_config.py \
          --no-browser
      Restart=always
      RestartSec=5

      [Install]
      WantedBy=multi-user.target

  # nginx reverse proxy — auth_basic defense-in-depth
  # /health and kernel API paths are auth-exempt (readiness probe + WebSocket)
  - path: /etc/nginx/sites-available/jupyterlab
    permissions: '0644'
    content: |
      map $http_upgrade $connection_upgrade {
          default upgrade;
          '' close;
      }
      server {
          listen 8080;
          server_name _;
          client_max_body_size 500M;

          location /health { return 200 'ok'; add_header Content-Type text/plain; }

          location ~* ^/(api|files|nbconvert|terminals|kernels|sessions|metrics)/ {
              auth_basic off;
              proxy_pass http://127.0.0.1:8888;
              proxy_http_version 1.1;
              proxy_set_header Upgrade $http_upgrade;
              proxy_set_header Connection $connection_upgrade;
              proxy_set_header Host $host;
              proxy_set_header Origin $http_origin;
              proxy_buffering off;
              proxy_read_timeout 3600s;
          }

          location / {
              auth_basic ""JupyterLab"";
              auth_basic_user_file /etc/nginx/.htpasswd;
              proxy_pass http://127.0.0.1:8888;
              proxy_http_version 1.1;
              proxy_set_header Upgrade $http_upgrade;
              proxy_set_header Connection $connection_upgrade;
              proxy_set_header Host $host;
              proxy_set_header Origin $http_origin;
              proxy_buffering off;
              proxy_read_timeout 3600s;
          }
      }

  # MOTD — ${DECLOUD_DOMAIN} and ${DECLOUD_PASSWORD} are substituted by the
  # orchestrator before cloud-init parses this file, so they resolve correctly.
  - path: /etc/motd
    permissions: '0644'
    content: |
      ╯═══════════════════════════════════════════════════════════════╗
      ║         PyTorch + JupyterLab — DeCloud GPU Template          ║
      ╠═══════════════════════════════════════════════════════════════╣
      ║  URL:      https://${DECLOUD_DOMAIN}                             ║
      ║  Username: admin                                                 ║
      ║  Password: ${DECLOUD_PASSWORD}                                   ║
      ║                                                                  ║
      ║  GPU proxy env:   /etc/decloud/gpu-proxy.env                    ║
      ║  Notebooks:       /opt/notebooks/                               ║
      ║  Python venv:     /opt/jupyter/venv/bin/python3                 ║
      ║                                                                  ║
      ║  Limitations (proxy mode):                                       ║
      ║    ✗ torch.compile() / inductor (TORCHINDUCTOR_DISABLE=1)       ║
      ║    ✗ CUDA Graphs    (DECLOUD_GPU_GRAPH_NOOP=1)                  ║
      ║    ✓ Eager ops, transformers, LoRA, bitsandbytes 4-bit          ║
      ║                                                                  ║
      ║  Services:                                                       ║
      ║    systemctl status jupyterlab                                   ║
      ║    journalctl -u jupyterlab -f                                   ║
      ║                                                                  ║
      ╘═══════════════════════════════════════════════════════════════╝

runcmd:
  - systemctl enable --now qemu-guest-agent
  - mkdir -p /opt/jupyter /opt/notebooks

  # Install PyTorch 2.3.1 + CUDA 12.1 wheels
  - /usr/bin/python3 -m venv /opt/jupyter/venv
  - |
    /opt/jupyter/venv/bin/pip install --upgrade pip --quiet
    /opt/jupyter/venv/bin/pip install \
      torch==2.3.1+cu121 torchvision==0.18.1+cu121 torchaudio==2.3.1+cu121 \
      --index-url https://download.pytorch.org/whl/cu121 --quiet

  # Install ML stack
  - |
    /opt/jupyter/venv/bin/pip install \
      jupyterlab==4.2.5 ipywidgets \
      numpy pandas matplotlib seaborn scipy scikit-learn \
      transformers==4.44.2 accelerate==0.33.0 \
      datasets tokenizers sentencepiece huggingface_hub tqdm Pillow \
      --quiet

  # bitsandbytes for 4-bit/8-bit quantization
  - |
    /opt/jupyter/venv/bin/pip install bitsandbytes==0.43.3 --quiet \
      || echo 'bitsandbytes install failed — skipping'

  # Disable cuDNN globally — Bug 19 parked (export table context struct unknown).
  # .pth file auto-imports the patch module on every Python startup.
  # Default arg _orig=_orig_lazy_init captures the reference before patching
  # (required — .pth closures lose outer scope variables).
  - printf 'try:\n    import torch\n    _orig_lazy_init = torch.cuda._lazy_init\n    def _patched_lazy_init(_orig=_orig_lazy_init):\n        _orig()\n        torch.backends.cudnn.enabled = False\n    torch.cuda._lazy_init = _patched_lazy_init\nexcept ImportError:\n    pass\n' > /opt/jupyter/venv/lib/python3.10/site-packages/decloud_cudnn_disable.py
  - echo ""import decloud_cudnn_disable"" > /opt/jupyter/venv/lib/python3.10/site-packages/decloud_cudnn_disable.pth

  # Configure JupyterLab password.
  # Uses echo commands instead of a heredoc — heredoc content at column 0
  # breaks the cloud-init YAML parser and silently discards the entire config.
  - mkdir -p /root/.jupyter
  - |
    # JupyterLab auth is disabled — nginx basic auth is the access gate.
    # jupyter_server 2.x requires a separate login cookie for WebSocket kernel
    # channels even after nginx auth passes. Removing the password avoids this
    # double-auth problem. Security is enforced by nginx basic auth above.
    {
      echo ""c.ServerApp.ip = '0.0.0.0'""
      echo 'c.ServerApp.port = 8888'
      echo 'c.ServerApp.open_browser = False'
      echo ""c.ServerApp.root_dir = '/opt/notebooks'""
      echo 'c.ServerApp.allow_root = True'
      echo ""c.ServerApp.token = ''""
      echo ""c.ServerApp.password = ''""
      echo ""c.ServerApp.allow_origin = '*'""
      echo ""c.ServerApp.allow_origin_pat = '.*'""
      echo ""c.IdentityProvider.token = ''""
    } > /root/.jupyter/jupyter_lab_config.py

  - systemctl daemon-reload
  - systemctl enable --now jupyterlab

  # nginx — htpasswd auth (apache2-utils installed in packages above)
  - htpasswd -bc /etc/nginx/.htpasswd admin ${DECLOUD_PASSWORD}
  - rm -f /etc/nginx/sites-enabled/default
  - ln -sf /etc/nginx/sites-available/jupyterlab /etc/nginx/sites-enabled/jupyterlab
  - nginx -t && systemctl restart nginx && systemctl enable nginx

final_message: |
  PyTorch + JupyterLab is ready!
  URL:      https://${DECLOUD_DOMAIN}
  Username: admin
  Password: ${DECLOUD_PASSWORD}
  Open welcome.ipynb to verify GPU access.
  Note: torch.compile() is disabled (requires kernel driver, not available in proxy mode).
  Note: CUDA Graphs are disabled (noop stubs — use eager mode).";

    // ════════════════════════════════════════════════════════════════════════
    // Compose-pipeline tenant templates
    // ════════════════════════════════════════════════════════════════════════
    // Per UNIFIED_CLOUDINIT_PIPELINE.md §11, marketplace templates that declare
    // Variables opt into the compose pipeline. Each migrated template:
    //   1. Has a role layer in DeCloud.Builds/tenant-vms/{slug}/cloud-init.yaml.
    //   2. Adds a {RoleUrl, TemplateRevision} pair near the top of this file.
    //   3. Adds a Build*TemplateAsync method here that fetches + composes +
    //      declares Variables, returning a VmTemplate.
    //   4. Calls UpsertComposeTemplateAsync from SeedComposeTenantTemplatesAsync.
    //
    // Legacy templates (inline string CloudInitTemplate, no declared Variables)
    // continue to live in GetSeedTemplates() above and use the legacy
    // ${VARNAME} substitution path in VmService.TryScheduleVmAsync. Migration
    // is opt-in per template.

    private async Task SeedComposeTenantTemplatesAsync()
    {
        var ct = CancellationToken.None;

        // Each template is wrapped in its own try/catch so one fetch/compose
        // failure (e.g. transient GitHub raw outage) does not block other
        // compose templates from seeding. Pattern mirrors the outer TrySeed
        // failure-domain isolation in SeedAsync().
        await TryUpsertComposeAsync("ai-chatbot-ollama",
            () => BuildAiChatbotTemplateAsync(ct), ct);
        await TryUpsertComposeAsync("minecraft-paper",
            () => BuildMinecraftPaperTemplateAsync(ct), ct);
        await TryUpsertComposeAsync("coolify",
            () => BuildCoolifyTemplateAsync(ct), ct);
        await TryUpsertComposeAsync("leaderboard",
            () => BuildLeaderboardTemplateAsync(ct), ct);
        await TryUpsertComposeAsync("repo-deploy",
            () => BuildRepoDeployTemplateAsync(ct), ct);
    }

    private async Task TryUpsertComposeAsync(
        string slugLabel,
        Func<Task<VmTemplate>> build,
        CancellationToken ct)
    {
        try
        {
            await UpsertComposeTemplateAsync(await build(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Compose tenant template '{Slug}' seed failed. Other compose " +
                "templates will continue. Template will not be available until " +
                "the underlying issue is fixed and the orchestrator is restarted.",
                slugLabel);
        }
    }

    /// <summary>
    /// Revision-aware upsert for compose-pipeline tenant templates. Mirrors
    /// TenantVmTemplateSeeder.SeedTemplateAsync — skip if stored revision is
    /// ≥ seeder revision; preserve MongoDB _id on update so deployments using
    /// this template see the new revision via the normal cache invalidation.
    /// </summary>
    private async Task UpsertComposeTemplateAsync(VmTemplate template, CancellationToken ct)
    {
        var existing = await _dataStore.GetTemplateBySlugAsync(template.Slug);

        if (existing is not null && existing.Revision >= template.Revision)
        {
            _logger.LogDebug(
                "Compose tenant template '{Slug}' r{Stored} ≥ seeder r{New} — skipping",
                template.Slug, existing.Revision, template.Revision);
            return;
        }

        if (existing is not null)
        {
            template.Id = existing.Id;  // preserve MongoDB _id — update in-place
            _logger.LogInformation(
                "Updating compose tenant template '{Slug}': r{Old} → r{New}",
                template.Slug, existing.Revision, template.Revision);
        }
        else
        {
            _logger.LogInformation(
                "Seeding new compose tenant template '{Slug}' r{Rev}",
                template.Slug, template.Revision);
        }

        await _dataStore.SaveTemplateAsync(template);

        _logger.LogInformation(
            "✓ Compose tenant template '{Slug}' seeded " +
            "(r{Rev}, {VarCount} declared variables)",
            template.Slug,
            template.Revision,
            template.Variables.Count);
    }

    // ── AI Chatbot (Ollama + Open WebUI) ─────────────────────────────────

    /// <summary>
    /// Build the AI Chatbot (Ollama + Open WebUI) template by composing
    /// base-tenant.yaml + tenant-vms/ai-chatbot/cloud-init.yaml. The role
    /// layer carries only Ollama-specific content; SSH password auth,
    /// qemu-guest-agent, sshd_config drop-in, and chpasswd bootcmd are
    /// provided by base-tenant.yaml.
    /// </summary>
    private async Task<VmTemplate> BuildAiChatbotTemplateAsync(CancellationToken ct)
    {
        var baseLayer = await _httpClient.GetStringAsync(BaseTenantUrl, ct);
        var roleLayer = await _httpClient.GetStringAsync(AiChatbotRoleUrl, ct);

        var composed = TemplateComposer.Compose(
            baseLayer, roleLayer,
            baseName: "base-tenant.yaml",
            roleName: "tenant-vms/ai-chatbot/cloud-init.yaml");

        return new VmTemplate
        {
            Slug = "ai-chatbot-ollama",
            Name = "AI Chatbot (Ollama + Open WebUI)",
            Version = "2.0.0",
            Revision = AiChatbotTemplateRevision,
            Category = "ai-ml",

            Description =
                "Self-hosted ChatGPT alternative. Run AI models (Llama, Mistral, " +
                "Gemma) locally with a beautiful chat interface. No data leaves " +
                "your server.",

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

## Getting Started
1. Wait for initial setup to complete (~5-10 minutes for model download)
2. Open `https://__DECLOUD_DOMAIN__` in your browser
3. Sign up on first visit — you become admin
4. Select **llama3.2:3b** from the model dropdown
5. Start chatting!

## Pull More Models via SSH
```bash
ollama pull mistral         # 7B, great all-rounder
ollama pull gemma2:9b       # 9B, Google's model
ollama pull codellama       # 7B, optimized for code
ollama list                 # show installed models
```

## Architecture
nginx (:8080) → Open WebUI (:3000) → Ollama (:11434)",
            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://github.com/open-webui/open-webui",
            License = "MIT",

            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 3L * 1024 * 1024 * 1024,
                DiskBytes = 30L * 1024 * 1024 * 1024,
                GpuVramBytes = 3L * 1024 * 1024 * 1024,
                Constraints = new List<Constraint>
                {
                    new()
                    {
                        Target   = ConstraintTargets.Node.Gpu.Present,
                        Operator = ConstraintOperators.Eq,
                        Value    = true
                    }
                }
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 8,
                MemoryBytes = 16L * 1024 * 1024 * 1024,
                DiskBytes = 50L * 1024 * 1024 * 1024,
                GpuVramBytes = 8L * 1024 * 1024 * 1024
            },

            RequiresGpu = true,
            DefaultGpuMode = GpuMode.Passthrough,
            GpuRequirement = "Optional — NVIDIA GPU with CUDA dramatically improves inference speed",
            ContainerImage = "ollama/ollama:latest",

            Tags = new List<string>
            {
                "ai", "chatbot", "ollama", "open-webui", "llm", "llama",
                "mistral", "self-hosted", "private-ai", "chatgpt-alternative"
            },

            CloudInitTemplate = composed,
            Variables = BuildAiChatbotVariables(),

            DefaultEnvironmentVariables = new Dictionary<string, string>
            {
                ["DEFAULT_MODELS"] = "llama3.2:3b",
                // GPU proxy: application env vars (propagated to /etc/decloud/gpu-proxy.env
                // by LibvirtVmManager.EnsureGpuProxyShim()).
                ["GGML_CUDA_FORCE_MMQ"] = "1",
                ["GGML_CUDA_DISABLE_GRAPHS"] = "1",
                ["GGML_CUDA_NO_PEER_COPY"] = "1",
                ["DECLOUD_GPU_GRAPH_NOOP"] = "1",
            },

            ExposedPorts = new List<TemplatePort>
            {
                new TemplatePort
                {
                    Port = 11434,
                    Protocol = "http",
                    Description = "Ollama LLM Engine",
                    // Declared so the platform can see the component doing the
                    // actual work — a dependency is a Service that isn't public.
                    // The probe runs in-guest via guest-exec (localhost:11434);
                    // IsPublic=false means no ingress exposure is added.
                    IsPublic = false,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/api/tags",
                        // Same slow-link budget as the WebUI check.
                        TimeoutSeconds = 7200
                    }
                },
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
                        // ~6-8 GB of resumable downloads; on Basic-tier /
                        // residential links (~1 MB/s) honest readiness can
                        // legitimately take >1h. TimedOut self-heals to Ready
                        // (VmReadinessMonitor RecheckInterval), so this only
                        // tunes when the warning appears, not correctness.
                        TimeoutSeconds = 7200
                    }
                }
            },

            DefaultAccessUrl = "https://__DECLOUD_DOMAIN__",
            DefaultUsername = "root",
            UseGeneratedPassword = true,

            EstimatedCostPerHour = 0.15m,
            DefaultBandwidthTier = BandwidthTier.Basic,

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

    /// <summary>
    /// Declared Variables for the AI Chatbot composed template. Every
    /// <c>__VARNAME__</c> placeholder in the composed cloud-init must appear
    /// here or CloudInitValidator will throw. Mirrors
    /// <see cref="TenantVmTemplateSeeder"/>'s declared set plus
    /// <c>DECLOUD_DOMAIN</c> (used in motd and final_message).
    /// </summary>
    private static List<TemplateVariable> BuildAiChatbotVariables() => new()
    {
        // Identity (resolved from ctx.Vm)
        new() { Name = "VM_ID",       Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID)." },
        new() { Name = "VM_NAME",     Kind = VariableKind.Static, Required = true,
                Description = "VM display name." },
        new() { Name = "HOSTNAME",    Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname for the VM (currently equals VM_NAME)." },

        // Platform context
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // SSH / password (resolved from ctx.Vm.Spec.SshPublicKey, UserSuppliedStatics)
        new() { Name = "CA_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk listing user SSH public keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for chpasswd.users (cloud-init 22.3+ format)." },
        new() { Name = "ADMIN_PASSWORD", Kind = VariableKind.Static,
                DefaultValue = "",
                Description =
                    "Plaintext root password. Set via UserSuppliedStatics " +
                    "[\"ADMIN_PASSWORD\"] at deploy time. Used by base-tenant.yaml's " +
                    "bootcmd chpasswd and by the role layer for WEBUI_SECRET_KEY " +
                    "and motd display." },
        new() { Name = "SSH_PASSWORD_AUTH", Kind = VariableKind.Static,
                DefaultValue = "false",
                Description =
                    "'true' or 'false' string for cloud-init's ssh_pwauth. " +
                    "Derived from ADMIN_PASSWORD presence." },

        // Role-layer addition — resolved by DeCloudDomainResolver
        // (Resolvers/PlatformCommon/DeCloudDomainResolver.cs). Registering a
        // resolver — rather than relying on UserSuppliedStatics fallback —
        // makes the variable platform-bound per design §2.4, so the deploy
        // form correctly hides the field.
        new() { Name = "DECLOUD_DOMAIN", Kind = VariableKind.Static,
                Required = true,
                Description =
                    "Default subdomain assigned by central ingress " +
                    "(e.g. ai-chatbot-0887.vms.stackfi.tech). Resolved by " +
                    "DeCloudDomainResolver from Vm.IngressConfig.DefaultSubdomain " +
                    "with fallback to {vm.Id}.{ingressService.BaseDomain}. " +
                    "Used in the motd block (final_message body is intentionally " +
                    "absent — see TemplateComposer block-scalar workaround note)." },
    };

    // ── Minecraft Paper Server ───────────────────────────────────────────

    /// <summary>
    /// Build the Minecraft Paper Server template by composing
    /// base-tenant.yaml + tenant-vms/minecraft-paper/cloud-init.yaml. The role
    /// layer carries only the Paper workload; SSH password auth,
    /// qemu-guest-agent, sshd_config drop-in, and chpasswd bootcmd are
    /// provided by base-tenant.yaml.
    /// </summary>
    private async Task<VmTemplate> BuildMinecraftPaperTemplateAsync(CancellationToken ct)
    {
        var baseLayer = await _httpClient.GetStringAsync(BaseTenantUrl, ct);
        var roleLayer = await _httpClient.GetStringAsync(MinecraftPaperRoleUrl, ct);

        var composed = TemplateComposer.Compose(
            baseLayer, roleLayer,
            baseName: "base-tenant.yaml",
            roleName: "tenant-vms/minecraft-paper/cloud-init.yaml");

        return new VmTemplate
        {
            Slug = "minecraft-paper",
            Name = "Minecraft Paper Server",
            // Bumped to 2.0.0 from the legacy 1.0.0 — major version because
            // the pipeline changed (inline → compose). Behaviour to the user
            // is the same. The legacy 1.0.0 record's MongoDB _id is preserved
            // by UpsertComposeTemplateAsync's slug-based upsert.
            Version = "1.0.0",
            Revision = MinecraftPaperTemplateRevision,
            Category = "gaming",

            Description =
                "Multiplayer Minecraft Java Edition server running Paper — " +
                "the fastest, most stable Minecraft server software. " +
                "Invite friends within minutes.",

            LongDescription = @"## Features
- **Paper** — the most widely used high-performance Minecraft server fork, compatible with all Spigot/Bukkit plugins
- Java 21 runtime with Aikar's tuned JVM flags for low GC pauses
- Auto-scales JVM heap to 75% of available RAM at startup
- Graceful shutdown saves the world before stopping
- RCON enabled for remote server management
- Systemd service — auto-restarts on crash, survives reboots

## Getting Started
1. Wait for setup (~2–3 minutes for Java install + Paper download)
2. Connect from Minecraft Java Edition → Multiplayer → Add Server
3. **Server Address:** `${DECLOUD_DOMAIN}:25565`
4. SSH access: `ssh root@${DECLOUD_DOMAIN}` (password: `${DECLOUD_PASSWORD}`)

## Choosing a Version
The `Minecraft Version` field accepts any version with a PaperMC build, e.g. `1.21.4`, `1.20.6`, `1.19.4`.
Check available versions at https://papermc.io/downloads/paper.

## Server Management
````bash
# View live server console
journalctl -u minecraft -f

# Graceful save + stop / start
systemctl stop minecraft
systemctl start minecraft

# RCON console (local)
RCON_PASS=$(cat /opt/minecraft/.rcon-password)
python3 /opt/minecraft/rcon.py ""$RCON_PASS"" ""op YourUsername""
````

## Plugins
Drop `.jar` files into `/opt/minecraft/plugins/`, then restart:
````bash
systemctl restart minecraft
````

## Backups
World data lives at `/opt/minecraft/world/`. To back up:
````bash
tar czf ~/world-backup-$(date +%Y%m%d).tar.gz /opt/minecraft/world/
```",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://papermc.io/",
            License = "MIT",

            Tags = new List<string>
            {
                "minecraft", "gaming", "java", "multiplayer", "paper", "sandbox"
            },

            // ── Specs ─────────────────────────────────────────────────────
            // Vanilla + a few players: 2 vCPU / 4 GB works.
            // Paper with plugins or 10+ players: 4 vCPU / 8 GB recommended.
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 4L * 1024 * 1024 * 1024, //  4 GB
                DiskBytes = 20L * 1024 * 1024 * 1024, // 20 GB
            },
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024, //  8 GB
                DiskBytes = 50L * 1024 * 1024 * 1024, // 50 GB
            },

            RequiresGpu = false,

            // ── Ports ─────────────────────────────────────────────────────
            ExposedPorts = new List<TemplatePort>
            {
                new()
                {
                    Port        = 25565,
                    Protocol    = "tcp",
                    Description = "Minecraft Java Edition",
                    IsPublic    = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        // TCP connect succeeds as soon as the server is
                        // accepting connections. Paper takes ~60-90 s for
                        // world generation on first boot; 300 s is generous.
                        Strategy       = CheckStrategy.TcpPort,
                        TimeoutSeconds = 300,
                    }
                }
            },

            DefaultAccessUrl = null,  // Minecraft is not HTTP
            UseGeneratedPassword = true,

            EstimatedCostPerHour = 0.08m,
            DefaultBandwidthTier = BandwidthTier.Standard,

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            // Set IsVerified to false until the compose-pipeline migration is
            // field-validated (see VALIDATION.md). Flip to true after a
            // successful end-to-end deploy with a real Minecraft client
            // connection. Avoids mis-signalling "platform verified" before
            // the migrated template has had a real test.
            IsVerified = true,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,

            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,

            CloudInitTemplate = composed,
            Variables = BuildMinecraftPaperVariables(),
        };
    }

    /// <summary>
    /// Declared Variables for the Minecraft Paper composed template. Every
    /// <c>__VARNAME__</c> placeholder in the composed cloud-init must appear
    /// here. Mirrors <see cref="BuildAiChatbotVariables"/> plus the five
    /// Minecraft-specific user statics (version, MOTD, max players, etc.).
    /// </summary>
    private static List<TemplateVariable> BuildMinecraftPaperVariables() => new()
    {
        // ── Identity / platform context (resolved by platform-common resolvers) ──
        new() { Name = "VM_ID",            Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID). Used in /etc/ssh/auth_principals/root by base-tenant.yaml." },
        new() { Name = "VM_NAME",          Kind = VariableKind.Static, Required = true,
                Description = "VM display name." },
        new() { Name = "HOSTNAME",         Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname for the VM. Referenced in setup.sh log and final_message." },
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // ── SSH / password (platform statics, resolved by registered resolvers) ──
        new() { Name = "CA_PUBLIC_KEY",              Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK",  Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk listing user SSH public keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK",      Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for chpasswd.users (cloud-init 22.3+ format)." },
        new() { Name = "ADMIN_PASSWORD",             Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "Plaintext root password. Set via UseGeneratedPassword pipeline at deploy time. Used by base-tenant.yaml's chpasswd bootcmd and referenced in final_message." },
        new() { Name = "SSH_PASSWORD_AUTH",          Kind = VariableKind.Static,
                DefaultValue = "false",
                Description = "'true' or 'false' for cloud-init's ssh_pwauth. Derived from ADMIN_PASSWORD presence." },

        // ── User-supplied statics — surface in the deploy form ────────────────
        new()
        {
            Name         = "MINECRAFT_VERSION",
            Kind         = VariableKind.Static,
            Required     = false,
            DefaultValue = "1.21.4",
            Description  = "Minecraft Java Edition version. Must have a PaperMC build. " +
                           "Check https://papermc.io/downloads/paper for available versions.",
        },
        new()
        {
            Name         = "SERVER_MOTD",
            Kind         = VariableKind.Static,
            Required     = false,
            DefaultValue = "A DeCloud Minecraft Server",
            Description  = "Message shown below the server name in the multiplayer list. " +
                           "Single line. Avoid special shell characters.",
        },
        new()
        {
            Name         = "MAX_PLAYERS",
            Kind         = VariableKind.Static,
            Required     = false,
            DefaultValue = "20",
            Description  = "Maximum number of simultaneous players. Scale with RAM: " +
                           "4 GB → 20 players, 8 GB → 50 players, 16 GB → 100+ players.",
        },
        new()
        {
            Name         = "DIFFICULTY",
            Kind         = VariableKind.Static,
            Required     = false,
            DefaultValue = "normal",
            Description  = "Server difficulty: peaceful, easy, normal, or hard.",
        },
        new()
        {
            Name         = "GAMEMODE",
            Kind         = VariableKind.Static,
            Required     = false,
            DefaultValue = "survival",
            Description  = "Default gamemode for new players: survival, creative, adventure, or spectator.",
        },
    };

    private async Task<VmTemplate> BuildCoolifyTemplateAsync(CancellationToken ct)
    {
        var baseLayer = await _httpClient.GetStringAsync(BaseTenantUrl, ct);
        var roleLayer = await _httpClient.GetStringAsync(CoolifyRoleUrl, ct);

        var composed = TemplateComposer.Compose(
            baseLayer, roleLayer,
            baseName: "base-tenant.yaml",
            roleName: "tenant-vms/coolify/cloud-init.yaml");

        return new VmTemplate
        {
            Slug = "coolify",
            Name = "Coolify (self-hosted PaaS)",
            Version = "1.0.0",
            Revision = CoolifyTemplateRevision,
            Category = "dev-tools",

            Description =
                "Your own Heroku/Vercel. Deploy apps, databases, and services from " +
                "Git with automatic HTTPS — a self-hosted PaaS on infrastructure you " +
                "control. No vendor lock-in, no platform fees, no KYC.",

            LongDescription = @"## Your own deploy platform

Coolify turns this VM into a self-hosted PaaS — a Heroku/Vercel/Railway
alternative. Connect a Git repo, push, and your app is live with automatic
HTTPS. One VM, unlimited apps.

## Features
- **Git-based deploys** — push to deploy; auto-detected build (Nixpacks/Dockerfile)
- **One-click databases** — PostgreSQL, MySQL, Redis, MongoDB with persistent volumes
- **Automatic HTTPS** — Let's Encrypt certificates handled for you
- **280+ one-click services** — Ghost, Plausible, Uptime Kuma, n8n, and more
- **No platform fees, no lock-in** — you own the server and the data

## Getting Started
1. Wait for setup (~2-5 minutes — Coolify pulls Docker + its own containers)
2. Open the dashboard at `https://__DECLOUD_DOMAIN__`
3. Your admin account was created automatically. Retrieve the login over SSH:
   `ssh root@__HOSTNAME__` then `cat /opt/decloud/coolify-admin`
4. Connect GitHub/GitLab and deploy your first app

## Making your apps reachable (custom domains)

Hostnames are yours to choose; ports are handled automatically. Never type an
app's internal port anywhere, and ignore the `sslip.io` URLs Coolify
auto-generates — they are not reachable on DeCloud.

**1. DNS — once per app, at your DNS provider:**

| Record | Name | Target |
|---|---|---|
| CNAME | `*.myapp.example.com` | `vms.stackfi.tech` |
| CNAME | `myapp.example.com` | `vms.stackfi.tech` |

The wildcard covers `api.`, `files.`, and any future service hostname; the
second record is needed because a DNS wildcard does not cover the bare name.

**2. DeCloud — this VM → Custom Domains:**
Add each hostname a browser will visit, target port **80**, then Verify.
Port 80 is Coolify's internal router (Traefik) — it forwards by hostname to
the right container and port from there.

**3. Coolify — each service's domain, with no port suffix:**
- Frontend: `https://myapp.example.com`
- Backend/API: `https://api.myapp.example.com`

**4. Internal services need nothing.** Databases, caches, and server-side
object storage are reached by other containers via their compose service name
(e.g. `mysql:3306`) — no domain, no exposure.

HTTPS certificates are issued automatically on the first request to each
verified hostname.

## Security
The dashboard has full control of this VM (it runs Docker and deploys code).
Your admin account is **pre-created at boot** and the public registration page
is disabled, so nobody can claim the instance before you. Keep the credential
from `/opt/decloud/coolify-admin` safe, and change it from Settings after first
login if you wish.

## Specs
- Minimum: 2 vCPU / 4 GB RAM — runs Coolify + one or two small apps
- Recommended: 4 vCPU / 8 GB — comfortable for several apps + databases
- Builds are CPU-intensive (Nixpacks compiles your source); more cores = faster builds",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://coolify.io/",
            License = "Apache-2.0",

            Tags = new List<string>
        {
            "paas", "deploy", "docker", "heroku", "self-hosted", "devops", "git"
        },

            // Coolify min is 2 GB; 4 GB is the realistic floor once you host apps.
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 4L * 1024 * 1024 * 1024,   //  4 GB
                DiskBytes = 40L * 1024 * 1024 * 1024,  // 40 GB
                ImageId = "ubuntu-24.04",
            },
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,   //  8 GB
                DiskBytes = 80L * 1024 * 1024 * 1024,  // 80 GB
                ImageId = "ubuntu-24.04",
            },

            RequiresGpu = false,

            ExposedPorts = new List<TemplatePort>
        {
            new()
            {
                Port        = 8000,
                Protocol    = "http",
                Description = "Coolify Dashboard",
                IsPublic    = true,
                ReadinessCheck = new ServiceCheck
                {
                    // Coolify pulls several images + runs DB migrations on
                    // first boot; allow generous time. "/up" is Laravel's
                    // health endpoint and returns 200 once migrations finish.
                    // Plain HttpGet works here because the dashboard accepts
                    // any Host header — no Traefik in the path.
                    Strategy       = CheckStrategy.HttpGet,
                    HttpPath       = "/up",
                    TimeoutSeconds = 1200,
                },
            }
        },

            DefaultAccessUrl = "https://__DECLOUD_DOMAIN__",
            DefaultUsername = "root",      // SSH user; Coolify admin is separate (see cred file)
            UseGeneratedPassword = true,        // root SSH password (Coolify admin is generated in setup.sh)

            EstimatedCostPerHour = 0.12m,
            DefaultBandwidthTier = BandwidthTier.Standard,

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            // Field-validate before signalling platform-verified — same discipline
            // as the Minecraft migration. Flip to true after Gates 0-4 pass.
            IsVerified = false,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,

            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,

            CloudInitTemplate = composed,
            Variables = BuildCoolifyVariables(),
        };
    }

    private static List<TemplateVariable> BuildCoolifyVariables() => new()
    {
        // Identity (resolved from ctx.Vm)
        new() { Name = "VM_ID",          Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID). Used by base-tenant.yaml." },
        new() { Name = "VM_NAME",        Kind = VariableKind.Static, Required = true,
                Description = "VM display name." },
        new() { Name = "HOSTNAME",       Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname. Referenced in coolify-setup.sh and final_message." },

        // Platform context
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // SSH / password machinery — ALL FOUR required by base-tenant.yaml.
        // SSH_AUTHORIZED_KEYS_BLOCK and PASSWORD_CONFIG_BLOCK are the resolved
        // YAML chunks the base layer renders (__SSH_AUTHORIZED_KEYS_BLOCK__ /
        // __PASSWORD_CONFIG_BLOCK__). Omitting them makes CloudInitValidator throw
        // "[Undeclared placeholders]" at render time.
        new() { Name = "CA_PUBLIC_KEY",  Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk listing user SSH public keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for chpasswd.users (cloud-init 22.3+ format)." },
        new() { Name = "ADMIN_PASSWORD", Kind = VariableKind.Static, DefaultValue = "",
                Description = "Plaintext root SSH password. Set via UseGeneratedPassword " +
                              "pipeline at deploy time. Used by base-tenant.yaml's chpasswd bootcmd." },
        new() { Name = "SSH_PASSWORD_AUTH", Kind = VariableKind.Static, DefaultValue = "false",
                Description = "'true'/'false' for cloud-init ssh_pwauth. Derived from ADMIN_PASSWORD presence." },

        // Role-layer addition — resolved by DeCloudDomainResolver
        new() { Name = "DECLOUD_DOMAIN", Kind = VariableKind.Static, Required = true,
                Description = "Assigned CentralIngress subdomain. Used in DefaultAccessUrl, " +
                              "dashboard URL, and final_message." },
    };


    // ── Generic Leaderboard ───────────────────────────────────────────────

    /// <summary>
    /// Build the Generic Leaderboard template by composing
    /// base-tenant.yaml + tenant-vms/leaderboard/cloud-init.yaml. The role layer
    /// carries only the leaderboard workload (a Python stdlib service embedded
    /// inline in write_files, same delivery as minecraft's setup.sh); SSH
    /// password auth, qemu-guest-agent, sshd CA, and chpasswd bootcmd come from
    /// base-tenant.yaml.
    /// </summary>
    private async Task<VmTemplate> BuildLeaderboardTemplateAsync(CancellationToken ct)
    {
        var baseLayer = await _httpClient.GetStringAsync(BaseTenantUrl, ct);
        var roleLayer = await _httpClient.GetStringAsync(LeaderboardRoleUrl, ct);

        var composed = TemplateComposer.Compose(
            baseLayer, roleLayer,
            baseName: "base-tenant.yaml",
            roleName: "tenant-vms/leaderboard/cloud-init.yaml");

        return new VmTemplate
        {
            Slug = "leaderboard",
            Name = "Generic Leaderboard",
            Version = "1.0.0",
            Revision = LeaderboardTemplateRevision,
            Category = "web-apps",

            Description =
                "Self-hosted leaderboard backend with a LootLocker-compatible " +
                "API. Project-wide boards, per-board access keys, top-N / " +
                "recent / rank-around-me queries.",

            LongDescription = @"## Generic leaderboard backend

One VM is one project. Boards are project-wide; each board ranks many members.
The HTTP API mirrors LootLocker's server leaderboard API, so games using
LootLocker or a portal SDK (Playgama, CrazyGames, Poki) integrate with a thin
adapter.

## Model
- **Boards** are created by the operator and shared across the project. The
  board key is the public read capability.
- **Apps** are just labels (e.g. a game, a partner) - they hold no secret.
- **Access keys** are the write credential. Each key is a secret bound to ONE
  board under ONE app, carrying only the rights you grant. A leaked key can
  touch one board and do only what you allowed.

## Roles
- **Operator** (you): the deploy root password is the admin token
  (`x-admin-token`), or sign in to the browser console at the VM URL. Creates
  boards and apps, issues and revokes access keys. Board lifecycle is
  operator-only.
- **Access key** (`x-session-token`): writes to its one board - `submit`
  (default) and optionally `member:delete`.
- **Public**: board key only - read rankings, no auth.
- **Submit policy** (per board): server-only by default; a board can be set
  `allow_public_submit` so a browser game with no backend posts directly (use a
  submit-only key - the secret then lives in the client and the board is forgeable).

## Getting Started
1. Wait ~1-2 minutes for first boot.
2. Open `https://__DECLOUD_DOMAIN__/` and sign in with the deploy root
   password, or use the admin token over curl:
   ```bash
   # create a board
   curl -s https://__DECLOUD_DOMAIN__/admin/boards \
     -H ""x-admin-token: <DEPLOY_PASSWORD>"" \
     -d '{""name"":""Daily"",""direction_method"":""descending""}'
   # create an app, then issue a key bound to the board
   curl -s https://__DECLOUD_DOMAIN__/admin/apps \
     -H ""x-admin-token: <DEPLOY_PASSWORD>"" -d '{""label"":""my-game""}'
   curl -s https://__DECLOUD_DOMAIN__/admin/apps/<APP_ID>/keys \
     -H ""x-admin-token: <DEPLOY_PASSWORD>"" \
     -d '{""board_key"":""<KEY>"",""scopes"":[""submit""]}'
   ```
3. Submit scores from your server with the key secret in `x-session-token`.

## Endpoints
- `POST /leaderboards/{key}/submit`  `{member_id, score, metadata}`  (key: submit; browser-writable only if the board is public-submit)
- `DELETE /leaderboards/{key}/members/{member_id}`  (key: member:delete)
- `POST /admin/boards` accepts `write_policy` + `allow_public_submit`; `PATCH /admin/boards/{key}` changes either later (forward-only; existing scores untouched)
- Operators browse and edit entries in the console; `PUT /admin/boards/{key}/members/{member_id}` `{score, metadata}` sets a score (bypasses keep-best), `DELETE` removes a member
- `GET  /leaderboards/{key}/list?count=10&after=<cursor>`  (public)
- `GET  /leaderboards/{key}/member/{member_id}?around=3`  (public)

## Trust boundary
Authenticates the deployer's server, not end users. Verifying a portal player
token is your backend's job. Guarantees authenticated, persisted, ranked - never
that a score is legitimate. Submit from your server, not a game client.

## Scoring
- `direction_method`: `descending` (higher wins) | `ascending` (lower wins)
- `write_policy`: `keep_best` (default) | `overwrite` (latest) | `first` (lock to
  the first submission; later submits ignored - good for daily challenges)",

            AuthorId = "platform",
            AuthorName = "DeCloud",
            SourceUrl = "https://docs.lootlocker.com/game-systems/leaderboards/",
            License = "MIT",

            Tags = new List<string>
            {
                "leaderboard", "games", "scores", "ranking", "lootlocker", "api"
            },

            // Interpreted service, no build — modest floor.
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 1,
                MemoryBytes = 1L * 1024 * 1024 * 1024,   //  1 GB
                DiskBytes = 10L * 1024 * 1024 * 1024,  // 10 GB
                ImageId = "ubuntu-24.04",
            },
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 2L * 1024 * 1024 * 1024,   //  2 GB
                DiskBytes = 20L * 1024 * 1024 * 1024,  // 20 GB
                ImageId = "ubuntu-24.04",
            },

            RequiresGpu = false,

            ExposedPorts = new List<TemplatePort>
            {
                new()
                {
                    Port        = 8080,
                    Protocol    = "http",   // CentralIngress (Caddy + HTTPS); DB port never exposed
                    Description = "Leaderboard HTTP API",
                    IsPublic    = true,
                    ReadinessCheck = new ServiceCheck
                    {
                        Strategy       = CheckStrategy.HttpGet,
                        HttpPath       = "/health",
                        TimeoutSeconds = 120,
                    },
                }
            },

            // The DeCloud access link opens the authenticated admin console (login
            // with the root/deploy password). Readiness still probes /health above.
            DefaultAccessUrl = "https://__DECLOUD_DOMAIN__/",
            DefaultUsername = "root",
            // The admin token IS the root password (ADMIN_TOKEN=__ADMIN_PASSWORD__),
            // so a password must always be generated or the service refuses to
            // start. Same ADMIN_PASSWORD dependency the ai-chatbot template has.
            UseGeneratedPassword = true,

            EstimatedCostPerHour = 0.02m,
            DefaultBandwidthTier = BandwidthTier.Standard,

            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            IsFeatured = true,
            // Field-validate before signalling platform-verified (same discipline
            // as the Minecraft/Coolify migrations). Flip to true after gates pass.
            IsVerified = false,
            IsCommunity = false,
            PricingModel = TemplatePricingModel.Free,
            TemplatePrice = 0,

            AverageRating = 0,
            TotalReviews = 0,
            RatingDistribution = new int[5],

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,

            CloudInitTemplate = composed,
            Variables = BuildLeaderboardVariables(),
            Artifacts = BuildLeaderboardArtifacts(),
        };
    }

    private static List<TemplateVariable> BuildLeaderboardVariables() => new()
    {
        // Identity (resolved from ctx.Vm) — used by base-tenant.yaml.
        new() { Name = "VM_ID",          Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID). Used by base-tenant.yaml." },
        new() { Name = "VM_NAME",        Kind = VariableKind.Static, Required = true,
                Description = "VM display name. Used in base-tenant final_message." },
        new() { Name = "HOSTNAME",       Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname (currently equals VM_NAME)." },

        // Platform context.
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // SSH / password machinery — ALL FOUR required by base-tenant.yaml.
        // Omitting any makes CloudInitValidator throw "[Undeclared placeholders]".
        new() { Name = "CA_PUBLIC_KEY",  Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk listing user SSH public keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for chpasswd.users (cloud-init 22.3+ format)." },
        new() { Name = "ADMIN_PASSWORD", Kind = VariableKind.Static, DefaultValue = "",
                Description = "Plaintext root password. Set via UseGeneratedPassword " +
                              "pipeline at deploy time. Also the leaderboard admin token " +
                              "(ADMIN_TOKEN) and shown in motd." },
        new() { Name = "SSH_PASSWORD_AUTH", Kind = VariableKind.Static, DefaultValue = "false",
                Description = "'true'/'false' for cloud-init ssh_pwauth. Derived from ADMIN_PASSWORD presence." },

        // Role-layer addition — resolved by DeCloudDomainResolver.
        new() { Name = "DECLOUD_DOMAIN", Kind = VariableKind.Static, Required = true,
                Description = "Assigned CentralIngress subdomain. Used in DefaultAccessUrl and motd." },
    };

    private static List<TemplateArtifact> BuildLeaderboardArtifacts() => new()
    {
        // ── Inline (data: URI) ───────────────────────────────────────────
        TemplateArtifact.Artifact("leaderboard", "Leaderboard service (Python stdlib)",
            ArtifactType.Script,
            sha256: LeaderboardApiPySha256, sourceUrl: LeaderboardApiPyDataUri)
    };
}