using DeCloud.Orchestrator.Services;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace Orchestrator.Services.Tenant;

/// <summary>
/// Seeds the <c>platform-leaderboard</c> tenant VM template.
///
/// <para>
/// <b>Contract:</b> one VM hosts one project's leaderboard backend. The backend
/// exposes a LootLocker-compatible HTTP API for submitting scores, querying
/// rankings, and managing boards/access keys. Boards are project-wide;
/// each board has a read-only key (board key), write-only keys (access keys
/// per board/app), and operator-only management via admin token (the deploy
/// root password). Public read endpoints require only the board key;
/// submissions and mutations require appropriate access keys or admin token.
/// </para>
///
/// <para>
/// <b>Wiring:</b> add to <c>SeedAsync</c> alongside the other tenant
/// templates:
/// <code>
/// await SeedTemplateAsync(await BuildLeaderboardTemplateAsync(ct), ct);
/// </code>
/// Update path: edit <c>tenant-vms/leaderboard/cloud-init.yaml</c> in
/// DeCloud.Builds, then bump <see cref="LeaderboardTemplateRevision"/>.
/// No artifacts — the provision script and documentation live in the role
/// layer's write_files (cloud-init content, delivered over the same
/// trusted path).
/// </para>
/// </summary>
public sealed partial class TenantVmTemplateSeeder
{
    private const string LeaderboardRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/leaderboard/cloud-init.yaml";

    /// <summary>
    /// Bump when the role layer or this seeder's metadata changes in a way
    /// that should affect new deployments. Running VMs are not redeployed.
    /// </summary>
    private const int LeaderboardTemplateRevision = 1;

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
            Name = "Leaderboard",
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
                QualityTier = QualityTier.Burstable,
                VirtualCpuCores = 1,
                MemoryBytes = 1L * 1024 * 1024 * 1024,   //  1 GB
                DiskBytes = 5L * 1024 * 1024 * 1024,  // 5 GB
                ImageId = "debian-12",
            },
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 2L * 1024 * 1024 * 1024,   //  2 GB
                DiskBytes = 20L * 1024 * 1024 * 1024,  // 10 GB
                ImageId = "debian-12",
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
