using System.Security.Cryptography;
using DeCloud.Orchestrator.Services;
using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;

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
///     and three artifacts (<c>decloud-agent-amd64</c>,
///     <c>decloud-agent-arm64</c>, <c>general-api</c>, <c>general-index</c>).
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
public sealed class GeneralVmTemplateSeeder
{
    // ── Source pinning ───────────────────────────────────────────────────

    /// <summary>
    /// Git ref (branch, tag, or commit SHA) used to fetch cloud-init YAML and
    /// base-tenant.yaml from DeCloud.Builds at seed time. Pin this to the same
    /// logical version as <see cref="BinaryBaseUrl"/>. Update when the YAML
    /// changes and bump <see cref="GeneralTemplateRevision"/>.
    /// </summary>
    private const string CloudInitRef = "main";

    private const string CloudInitRawBase =
        $"https://raw.githubusercontent.com/bekirmfr/DeCloud.Builds/{CloudInitRef}";

    private const string BaseTenantUrl =
        $"{CloudInitRawBase}/base-templates/base-tenant.yaml";

    private const string GeneralRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/general/cloud-init.yaml";

    /// <summary>
    /// HTTPS base for binary artifacts (decloud-agent). Update when a new
    /// binary release is cut in DeCloud.Builds and bump
    /// <see cref="GeneralTemplateRevision"/>.
    /// </summary>
    private const string BinaryBaseUrl =
        "https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0";

    // ── Template revision ────────────────────────────────────────────────

    /// <summary>
    /// Bumped whenever the composed cloud-init or any artifact changes in a
    /// way that should trigger re-deployment for new tenant VMs created from
    /// this template. Existing running VMs are not affected by revision bumps.
    /// </summary>
    private const int GeneralTemplateRevision = 1;

    // ── Binary artifact constants ────────────────────────────────────────
    // From binaries/v1.0.0 release notes. Update both halves when the binary
    // is rebuilt; bump GeneralTemplateRevision.

    private const string DecloudAgentAmd64Sha256 =
        "530c33c349f4c55d17a4f7e6a328d60da09b1257c1ffd2c54c4efd9f3e3962a2";
    private const string DecloudAgentArm64Sha256 =
        "349ad8dd16c837a868d8385a693a8ee376f72948660f823e8e70f150bda142ac";

    private const long DecloudAgentAmd64Bytes = 5_087_232;  // 4.85 MB
    private const long DecloudAgentArm64Bytes = 4_915_200;  // 4.69 MB

    // ============================================================
    // artifact-constants.cs  —  AUTO-GENERATED
    // Run: bash tenant-vms/compute-artifact-constants.sh
    // DO NOT EDIT MANUALLY — regenerate from source files.
    //
    // Usage:
    //   1. Copy the constants for changed artifacts into GeneralVmTemplateSeeder.cs
    //      (or other tenant-side seeders).
    //   2. Bump the affected TemplateRevision constant (GeneralTemplateRevision, etc.)
    //   3. Commit to the Orchestrator repo
    // ============================================================

    // Paste this block inside the appropriate tenant template seeder class body.
    // Replace the COMPUTE_FROM_FILE placeholders with the generated values.

    // Generated: 2026-05-04T14:48:17Z

    // ── General ────────────────────────────────────────────────────────────────
    // general/assets/general-api.py  (2096 bytes)
    private const string GeneralApiPySha256 = "d8165148244b1cbc67f4a787806a5541afef2b15b0f92591ac7ae0d86c7e3dfd";
    private const string GeneralApiPyDataUri = "data:text/x-python;base64,IyEvdXNyL2Jpbi9lbnYgcHl0aG9uMw0KIiIiRGVDbG91ZCBWTSBXZWxjb21lIFBhZ2UgU2VydmVyIHdpdGggQ2FjaGluZyIiIg0KaW1wb3J0IGh0dHAuc2VydmVyDQppbXBvcnQgc29ja2V0c2VydmVyDQppbXBvcnQgb3MNCmltcG9ydCBoYXNobGliDQppbXBvcnQgdGltZQ0KZnJvbSBlbWFpbC51dGlscyBpbXBvcnQgZm9ybWF0ZGF0ZQ0KDQpQT1JUID0gODANCkRJUkVDVE9SWSA9ICIvdmFyL3d3dyINCg0KQ0FDSEVfRFVSQVRJT04gPSB7DQogICAgJy5odG1sJzogMzAwLCAnLmNzcyc6IDg2NDAwLCAnLmpzJzogODY0MDAsDQogICAgJy5qcGcnOiAyNTkyMDAwLCAnLnBuZyc6IDI1OTIwMDAsICcuc3ZnJzogMjU5MjAwMA0KfQ0KDQpjbGFzcyBDYWNoaW5nSGFuZGxlcihodHRwLnNlcnZlci5TaW1wbGVIVFRQUmVxdWVzdEhhbmRsZXIpOg0KICAgIGRlZiBfX2luaXRfXyhzZWxmLCAqYXJncywgKiprd2FyZ3MpOg0KICAgICAgICBzdXBlcigpLl9faW5pdF9fKCphcmdzLCBkaXJlY3Rvcnk9RElSRUNUT1JZLCAqKmt3YXJncykNCiAgICANCiAgICBkZWYgZW5kX2hlYWRlcnMoc2VsZik6DQogICAgICAgIHBhdGggPSBzZWxmLnRyYW5zbGF0ZV9wYXRoKHNlbGYucGF0aCkNCiAgICAgICAgaWYgb3MucGF0aC5leGlzdHMocGF0aCkgYW5kIG5vdCBvcy5wYXRoLmlzZGlyKHBhdGgpOg0KICAgICAgICAgICAgZXh0ID0gb3MucGF0aC5zcGxpdGV4dChwYXRoKVsxXS5sb3dlcigpDQogICAgICAgICAgICBkdXJhdGlvbiA9IENBQ0hFX0RVUkFUSU9OLmdldChleHQsIDM2MDApDQogICAgICAgICAgICANCiAgICAgICAgICAgIHNlbGYuc2VuZF9oZWFkZXIoJ0NhY2hlLUNvbnRyb2wnLCBmJ3B1YmxpYywgbWF4LWFnZT17ZHVyYXRpb259JykNCiAgICAgICAgICAgIA0KICAgICAgICAgICAgdHJ5Og0KICAgICAgICAgICAgICAgIHN0YXQgPSBvcy5zdGF0KHBhdGgpDQogICAgICAgICAgICAgICAgZXRhZyA9IGYnIntpbnQoc3RhdC5zdF9tdGltZSl9LXtzdGF0LnN0X3NpemV9IicNCiAgICAgICAgICAgICAgICBzZWxmLnNlbmRfaGVhZGVyKCdFVGFnJywgZXRhZykNCiAgICAgICAgICAgICAgICBzZWxmLnNlbmRfaGVhZGVyKCdMYXN0LU1vZGlmaWVkJywgZm9ybWF0ZGF0ZShzdGF0LnN0X210aW1lLCB1c2VnbXQ9VHJ1ZSkpDQogICAgICAgICAgICBleGNlcHQ6IHBhc3MNCiAgICAgICAgICAgIA0KICAgICAgICAgICAgc2VsZi5zZW5kX2hlYWRlcignWC1Db250ZW50LVR5cGUtT3B0aW9ucycsICdub3NuaWZmJykNCiAgICAgICAgDQogICAgICAgIHN1cGVyKCkuZW5kX2hlYWRlcnMoKQ0KICAgIA0KICAgIGRlZiBkb19HRVQoc2VsZik6DQogICAgICAgIHBhdGggPSBzZWxmLnRyYW5zbGF0ZV9wYXRoKHNlbGYucGF0aCkNCiAgICAgICAgaWYgb3MucGF0aC5leGlzdHMocGF0aCkgYW5kIG5vdCBvcy5wYXRoLmlzZGlyKHBhdGgpOg0KICAgICAgICAgICAgaWZfbm9uZV9tYXRjaCA9IHNlbGYuaGVhZGVycy5nZXQoJ0lmLU5vbmUtTWF0Y2gnKQ0KICAgICAgICAgICAgaWYgaWZfbm9uZV9tYXRjaDoNCiAgICAgICAgICAgICAgICB0cnk6DQogICAgICAgICAgICAgICAgICAgIHN0YXQgPSBvcy5zdGF0KHBhdGgpDQogICAgICAgICAgICAgICAgICAgIGV0YWcgPSBmJyJ7aW50KHN0YXQuc3RfbXRpbWUpfS17c3RhdC5zdF9zaXplfSInDQogICAgICAgICAgICAgICAgICAgIGlmIGlmX25vbmVfbWF0Y2ggPT0gZXRhZzoNCiAgICAgICAgICAgICAgICAgICAgICAgIHNlbGYuc2VuZF9yZXNwb25zZSgzMDQpDQogICAgICAgICAgICAgICAgICAgICAgICBzZWxmLnNlbmRfaGVhZGVyKCdFVGFnJywgZXRhZykNCiAgICAgICAgICAgICAgICAgICAgICAgIHNlbGYuZW5kX2hlYWRlcnMoKQ0KICAgICAgICAgICAgICAgICAgICAgICAgcmV0dXJuDQogICAgICAgICAgICAgICAgZXhjZXB0OiBwYXNzDQogICAgICAgIA0KICAgICAgICBzdXBlcigpLmRvX0dFVCgpDQoNCndpdGggc29ja2V0c2VydmVyLlRDUFNlcnZlcigoIiIsIFBPUlQpLCBDYWNoaW5nSGFuZGxlcikgYXMgaHR0cGQ6DQogICAgcHJpbnQoZiJEZUNsb3VkIFdlbGNvbWUgU2VydmVyIG9uIHBvcnQge1BPUlR9IikNCiAgICBodHRwZC5zZXJ2ZV9mb3JldmVyKCk=";

    // general/assets/index.html  (4313 bytes)
    private const string GeneralIndexHtmlSha256 = "c26378531c34c422081b2fb5951f73d1916466bf7a15c755a3f1da53fe768721";
    private const string GeneralIndexHtmlDataUri = "data:text/html;base64,PCFET0NUWVBFIGh0bWw+DQo8aHRtbD4NCjxoZWFkPg0KICAgIDx0aXRsZT5fX1ZNX05BTUVfXzwvdGl0bGU+DQogICAgPG1ldGEgbmFtZT0idmlld3BvcnQiIGNvbnRlbnQ9IndpZHRoPWRldmljZS13aWR0aCxpbml0aWFsLXNjYWxlPTEiPg0KICAgIDxsaW5rIHJlbD0icHJlY29ubmVjdCIgaHJlZj0iaHR0cHM6Ly9mb250cy5nb29nbGVhcGlzLmNvbSI+DQogICAgPGxpbmsgcmVsPSJwcmVjb25uZWN0IiBocmVmPSJodHRwczovL2ZvbnRzLmdzdGF0aWMuY29tIiBjcm9zc29yaWdpbj4NCiAgICA8bGluayBocmVmPSJodHRwczovL2ZvbnRzLmdvb2dsZWFwaXMuY29tL2NzczI/ZmFtaWx5PUpldEJyYWlucytNb25vOndnaHRANDAwOzUwMDs2MDAmZmFtaWx5PU91dGZpdDp3Z2h0QDMwMDs0MDA7NTAwOzYwMDs3MDAmZGlzcGxheT1zd2FwIiByZWw9InN0eWxlc2hlZXQiPg0KICAgIDxzdHlsZT4NCiAgICAgICAgKiB7DQogICAgICAgICAgICBtYXJnaW46IDA7DQogICAgICAgICAgICBwYWRkaW5nOiAwOw0KICAgICAgICAgICAgYm94LXNpemluZzogYm9yZGVyLWJveDsNCiAgICAgICAgfQ0KDQogICAgICAgIGJvZHkgew0KICAgICAgICAgICAgZm9udC1mYW1pbHk6ICdPdXRmaXQnLCAtYXBwbGUtc3lzdGVtLCBCbGlua01hY1N5c3RlbUZvbnQsIHNhbnMtc2VyaWY7DQogICAgICAgICAgICBtaW4taGVpZ2h0OiAxMDB2aDsNCiAgICAgICAgICAgIGRpc3BsYXk6IGZsZXg7DQogICAgICAgICAgICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsNCiAgICAgICAgICAgIGFsaWduLWl0ZW1zOiBjZW50ZXI7DQogICAgICAgICAgICBiYWNrZ3JvdW5kOiAjMGEwYjBmOw0KICAgICAgICAgICAgY29sb3I6ICNmMGYyZjU7DQogICAgICAgICAgICBwb3NpdGlvbjogcmVsYXRpdmU7DQogICAgICAgICAgICBvdmVyZmxvdzogaGlkZGVuOw0KICAgICAgICB9DQoNCiAgICAgICAgLyogQmFja2dyb3VuZCBFZmZlY3RzICovDQogICAgICAgIGJvZHk6OmJlZm9yZSB7DQogICAgICAgICAgICBjb250ZW50OiAnJzsNCiAgICAgICAgICAgIHBvc2l0aW9uOiBmaXhlZDsNCiAgICAgICAgICAgIGluc2V0OiAwOw0KICAgICAgICAgICAgYmFja2dyb3VuZC1pbWFnZTogbGluZWFyLWdyYWRpZW50KHJnYmEoMjU1LDI1NSwyNTUsMC4wMikgMXB4LCB0cmFuc3BhcmVudCAxcHgpLCANCiAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIGxpbmVhci1ncmFkaWVudCg5MGRlZywgcmdiYSgyNTUsMjU1LDI1NSwwLjAyKSAxcHgsIHRyYW5zcGFyZW50IDFweCk7DQogICAgICAgICAgICBiYWNrZ3JvdW5kLXNpemU6IDYwcHggNjBweDsNCiAgICAgICAgICAgIHBvaW50ZXItZXZlbnRzOiBub25lOw0KICAgICAgICAgICAgei1pbmRleDogMDsNCiAgICAgICAgfQ0KDQogICAgICAgIGJvZHk6OmFmdGVyIHsNCiAgICAgICAgICAgIGNvbnRlbnQ6ICcnOw0KICAgICAgICAgICAgcG9zaXRpb246IGZpeGVkOw0KICAgICAgICAgICAgd2lkdGg6IDYwMHB4Ow0KICAgICAgICAgICAgaGVpZ2h0OiA2MDBweDsNCiAgICAgICAgICAgIGJvcmRlci1yYWRpdXM6IDUwJTsNCiAgICAgICAgICAgIGJhY2tncm91bmQ6ICMwMGQ0YWE7DQogICAgICAgICAgICBmaWx0ZXI6IGJsdXIoMTIwcHgpOw0KICAgICAgICAgICAgb3BhY2l0eTogMC4xNTsNCiAgICAgICAgICAgIHRvcDogLTIwMHB4Ow0KICAgICAgICAgICAgcmlnaHQ6IC0xMDBweDsNCiAgICAgICAgICAgIHBvaW50ZXItZXZlbnRzOiBub25lOw0KICAgICAgICAgICAgei1pbmRleDogMDsNCiAgICAgICAgfQ0KDQogICAgICAgIC5jYXJkIHsNCiAgICAgICAgICAgIGJhY2tncm91bmQ6ICMxMjE0MWE7DQogICAgICAgICAgICBwYWRkaW5nOiAzcmVtOw0KICAgICAgICAgICAgYm9yZGVyLXJhZGl1czogMjRweDsNCiAgICAgICAgICAgIHRleHQtYWxpZ246IGNlbnRlcjsNCiAgICAgICAgICAgIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMjU1LCAyNTUsIDI1NSwgMC4xKTsNCiAgICAgICAgICAgIG1heC13aWR0aDogNTAwcHg7DQogICAgICAgICAgICBwb3NpdGlvbjogcmVsYXRpdmU7DQogICAgICAgICAgICB6LWluZGV4OiAxOw0KICAgICAgICAgICAgYm94LXNoYWRvdzogMCAyMHB4IDYwcHggcmdiYSgwLCAwLCAwLCAwLjUpOw0KICAgICAgICB9DQoNCiAgICAgICAgaDEgew0KICAgICAgICAgICAgZm9udC1zaXplOiAycmVtOw0KICAgICAgICAgICAgbWFyZ2luLWJvdHRvbTogMC41cmVtOw0KICAgICAgICAgICAgZm9udC13ZWlnaHQ6IDcwMDsNCiAgICAgICAgfQ0KDQogICAgICAgIC5uYW1lIHsNCiAgICAgICAgICAgIGNvbG9yOiAjMDBkNGFhOw0KICAgICAgICAgICAgZm9udC1mYW1pbHk6ICdKZXRCcmFpbnMgTW9ubycsIG1vbm9zcGFjZTsNCiAgICAgICAgICAgIGZvbnQtd2VpZ2h0OiA2MDA7DQogICAgICAgIH0NCg0KICAgICAgICBwIHsNCiAgICAgICAgICAgIGNvbG9yOiAjOWNhM2FmOw0KICAgICAgICAgICAgbWFyZ2luLXRvcDogMC41cmVtOw0KICAgICAgICB9DQoNCiAgICAgICAgLnN0YXR1cyB7DQogICAgICAgICAgICBkaXNwbGF5OiBpbmxpbmUtYmxvY2s7DQogICAgICAgICAgICBwYWRkaW5nOiAwLjRyZW0gMXJlbTsNCiAgICAgICAgICAgIGJhY2tncm91bmQ6IHJnYmEoMCwgMjEyLCAxNzAsIDAuMSk7DQogICAgICAgICAgICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDAsIDIxMiwgMTcwLCAwLjMpOw0KICAgICAgICAgICAgYm9yZGVyLXJhZGl1czogOTk5OXB4Ow0KICAgICAgICAgICAgbWFyZ2luLXRvcDogMXJlbTsNCiAgICAgICAgICAgIGZvbnQtc2l6ZTogMC44NzVyZW07DQogICAgICAgICAgICBjb2xvcjogIzAwZDRhYTsNCiAgICAgICAgfQ0KDQogICAgICAgIC5kb3Qgew0KICAgICAgICAgICAgZGlzcGxheTogaW5saW5lLWJsb2NrOw0KICAgICAgICAgICAgd2lkdGg6IDhweDsNCiAgICAgICAgICAgIGhlaWdodDogOHB4Ow0KICAgICAgICAgICAgYmFja2dyb3VuZDogIzAwZDRhYTsNCiAgICAgICAgICAgIGJvcmRlci1yYWRpdXM6IDUwJTsNCiAgICAgICAgICAgIG1hcmdpbi1yaWdodDogMC41cmVtOw0KICAgICAgICAgICAgYW5pbWF0aW9uOiBwdWxzZSAycyBpbmZpbml0ZTsNCiAgICAgICAgICAgIGJveC1zaGFkb3c6IDAgMCA4cHggcmdiYSgwLCAyMTIsIDE3MCwgMC41KTsNCiAgICAgICAgfQ0KDQogICAgICAgIEBrZXlmcmFtZXMgcHVsc2Ugew0KICAgICAgICAgICAgMCUsIDEwMCUgew0KICAgICAgICAgICAgICAgIG9wYWNpdHk6IDE7DQogICAgICAgICAgICB9DQoNCiAgICAgICAgICAgIDUwJSB7DQogICAgICAgICAgICAgICAgb3BhY2l0eTogMC41Ow0KICAgICAgICAgICAgfQ0KICAgICAgICB9DQoNCiAgICAgICAgLm5vdGUgew0KICAgICAgICAgICAgbWFyZ2luLXRvcDogMnJlbTsNCiAgICAgICAgICAgIHBhZGRpbmc6IDFyZW07DQogICAgICAgICAgICBiYWNrZ3JvdW5kOiAjMWExZDI2Ow0KICAgICAgICAgICAgYm9yZGVyOiAxcHggc29saWQgcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA2KTsNCiAgICAgICAgICAgIGJvcmRlci1yYWRpdXM6IDEwcHg7DQogICAgICAgICAgICBmb250LXNpemU6IDAuOHJlbTsNCiAgICAgICAgICAgIGNvbG9yOiAjNmI3MjgwOw0KICAgICAgICB9DQoNCiAgICAgICAgY29kZSB7DQogICAgICAgICAgICBiYWNrZ3JvdW5kOiAjMGEwYjBmOw0KICAgICAgICAgICAgcGFkZGluZzogMC4ycmVtIDAuNHJlbTsNCiAgICAgICAgICAgIGJvcmRlci1yYWRpdXM6IDZweDsNCiAgICAgICAgICAgIGZvbnQtc2l6ZTogMC43NXJlbTsNCiAgICAgICAgICAgIGNvbG9yOiAjZjU5ZTBiOw0KICAgICAgICAgICAgZm9udC1mYW1pbHk6ICdKZXRCcmFpbnMgTW9ubycsIG1vbm9zcGFjZTsNCiAgICAgICAgfQ0KICAgIDwvc3R5bGU+DQo8L2hlYWQ+DQo8Ym9keT4NCiAgICA8ZGl2IGNsYXNzPSJjYXJkIj4NCiAgICAgICAgPGgxPlZpcnR1YWwgTWFjaGluZSA8c3BhbiBjbGFzcz0ibmFtZSI+X19WTV9OQU1FX188L3NwYW4+PC9oMT4NCiAgICAgICAgPHA+RGVjZW50cmFsaXplZCBjb21wdXRlLCBydW5uaW5nIG9uIHlvdXIgdGVybXMuPC9wPg0KICAgICAgICA8ZGl2IGNsYXNzPSJzdGF0dXMiPg0KICAgICAgICAgICAgPHNwYW4gY2xhc3M9ImRvdCI+PC9zcGFuPk9ubGluZQ0KICAgICAgICA8L2Rpdj4NCiAgICAgICAgPGRpdiBjbGFzcz0ibm90ZSI+DQogICAgICAgICAgICBUbyByZW1vdmUgdGhpcyBwYWdlIGFuZCBmcmVlIHBvcnQgODA6PGJyPg0KICAgICAgICAgICAgPGNvZGU+c3VkbyBzeXN0ZW1jdGwgZGlzYWJsZSAtLW5vdyB3ZWxjb21lPC9jb2RlPg0KICAgICAgICA8L2Rpdj4NCiAgICA8L2Rpdj4NCjwvYm9keT4NCjwvaHRtbD4=";


    // ── Summary ─────────────────────────────────────────────────────────────────
    // Generated: 2026-05-04T14:48:17Z
    // Roles discovered:
    //   general/assets/ [prefix='General']: 2 files
    // Total: 2 artifact constants



    // ── Infrastructure ───────────────────────────────────────────────────

    private readonly DataStore _dataStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GeneralVmTemplateSeeder> _logger;

    public GeneralVmTemplateSeeder(
        DataStore dataStore,
        HttpClient httpClient,
        ILogger<GeneralVmTemplateSeeder> logger)
    {
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
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedTemplateAsync(await BuildGeneralTemplateAsync(ct), ct);
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
                "General-purpose tenant VM with the DeCloud agent pre-installed. " +
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
        // ── Binary (HTTPS — DeCloud.Builds release) ──────────────────────
        Artifact("decloud-agent", "DeCloud agent — amd64",
            ArtifactType.Binary, arch: "amd64",
            sha256: DecloudAgentAmd64Sha256, sizeBytes: DecloudAgentAmd64Bytes,
            sourceUrl: $"{BinaryBaseUrl}/decloud-agent-amd64"),

        Artifact("decloud-agent", "DeCloud agent — arm64",
            ArtifactType.Binary, arch: "arm64",
            sha256: DecloudAgentArm64Sha256, sizeBytes: DecloudAgentArm64Bytes,
            sourceUrl: $"{BinaryBaseUrl}/decloud-agent-arm64"),

        // ── Inline (data: URI) ───────────────────────────────────────────
        Artifact("general-api", "General-purpose VM dashboard API (Python)",
            ArtifactType.Script,
            sha256: GeneralApiPySha256, sourceUrl: GeneralApiPyDataUri),

        Artifact("general-index", "General-purpose VM dashboard HTML",
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

    // ── Artifact factory ─────────────────────────────────────────────────
    // Mirrors SystemVmTemplateSeeder.Artifact for consistency. If a third
    // seeder appears, refactor this and the SHA256 verification into a shared
    // helper class.

    private static TemplateArtifact Artifact(
        string name,
        string description,
        ArtifactType type,
        string sha256,
        string sourceUrl,
        string? arch = null,
        long sizeBytes = 0)
    {
        if (sourceUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            sha256 != "COMPUTE_FROM_FILE")
        {
            var commaIndex = sourceUrl.IndexOf(',');
            if (commaIndex >= 0)
            {
                var bytes = Convert.FromBase64String(sourceUrl[(commaIndex + 1)..].Trim());
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"GeneralVmTemplateSeeder: inline artifact '{name}' SHA256 mismatch. " +
                        $"Expected {sha256[..12]}, actual {actual[..12]}. " +
                        "Run compute-artifact-constants.sh to regenerate constants.");

                sizeBytes = bytes.Length;
            }
        }

        return new TemplateArtifact
        {
            Name = name,
            Description = description,
            Type = type,
            Architecture = arch,
            Sha256 = sha256,
            SizeBytes = sizeBytes,
            SourceUrl = sourceUrl,
            RegisteredAt = DateTime.UtcNow,
            RegisteredBy = "system",
        };
    }
}
