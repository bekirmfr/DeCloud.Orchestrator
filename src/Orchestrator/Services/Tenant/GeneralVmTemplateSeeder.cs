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

    // Generated: 2026-05-04T15:48:55Z

    // ── General ────────────────────────────────────────────────────────────────
    // general/assets/general-api.py  (2037 bytes)
    private const string GeneralApiPySha256 = "0d036202cc563074b2b518e2c0e5e01abbe0317e9e0369a0d9896063ba01bbcd";
    private const string GeneralApiPyDataUri = "data:text/x-python;base64,IyEvdXNyL2Jpbi9lbnYgcHl0aG9uMwoiIiJEZUNsb3VkIFZNIFdlbGNvbWUgUGFnZSBTZXJ2ZXIgd2l0aCBDYWNoaW5nIiIiCmltcG9ydCBodHRwLnNlcnZlcgppbXBvcnQgc29ja2V0c2VydmVyCmltcG9ydCBvcwppbXBvcnQgaGFzaGxpYgppbXBvcnQgdGltZQpmcm9tIGVtYWlsLnV0aWxzIGltcG9ydCBmb3JtYXRkYXRlCgpQT1JUID0gODAKRElSRUNUT1JZID0gIi92YXIvd3d3IgoKQ0FDSEVfRFVSQVRJT04gPSB7CiAgICAnLmh0bWwnOiAzMDAsICcuY3NzJzogODY0MDAsICcuanMnOiA4NjQwMCwKICAgICcuanBnJzogMjU5MjAwMCwgJy5wbmcnOiAyNTkyMDAwLCAnLnN2Zyc6IDI1OTIwMDAKfQoKY2xhc3MgQ2FjaGluZ0hhbmRsZXIoaHR0cC5zZXJ2ZXIuU2ltcGxlSFRUUFJlcXVlc3RIYW5kbGVyKToKICAgIGRlZiBfX2luaXRfXyhzZWxmLCAqYXJncywgKiprd2FyZ3MpOgogICAgICAgIHN1cGVyKCkuX19pbml0X18oKmFyZ3MsIGRpcmVjdG9yeT1ESVJFQ1RPUlksICoqa3dhcmdzKQogICAgCiAgICBkZWYgZW5kX2hlYWRlcnMoc2VsZik6CiAgICAgICAgcGF0aCA9IHNlbGYudHJhbnNsYXRlX3BhdGgoc2VsZi5wYXRoKQogICAgICAgIGlmIG9zLnBhdGguZXhpc3RzKHBhdGgpIGFuZCBub3Qgb3MucGF0aC5pc2RpcihwYXRoKToKICAgICAgICAgICAgZXh0ID0gb3MucGF0aC5zcGxpdGV4dChwYXRoKVsxXS5sb3dlcigpCiAgICAgICAgICAgIGR1cmF0aW9uID0gQ0FDSEVfRFVSQVRJT04uZ2V0KGV4dCwgMzYwMCkKICAgICAgICAgICAgCiAgICAgICAgICAgIHNlbGYuc2VuZF9oZWFkZXIoJ0NhY2hlLUNvbnRyb2wnLCBmJ3B1YmxpYywgbWF4LWFnZT17ZHVyYXRpb259JykKICAgICAgICAgICAgCiAgICAgICAgICAgIHRyeToKICAgICAgICAgICAgICAgIHN0YXQgPSBvcy5zdGF0KHBhdGgpCiAgICAgICAgICAgICAgICBldGFnID0gZicie2ludChzdGF0LnN0X210aW1lKX0te3N0YXQuc3Rfc2l6ZX0iJwogICAgICAgICAgICAgICAgc2VsZi5zZW5kX2hlYWRlcignRVRhZycsIGV0YWcpCiAgICAgICAgICAgICAgICBzZWxmLnNlbmRfaGVhZGVyKCdMYXN0LU1vZGlmaWVkJywgZm9ybWF0ZGF0ZShzdGF0LnN0X210aW1lLCB1c2VnbXQ9VHJ1ZSkpCiAgICAgICAgICAgIGV4Y2VwdDogcGFzcwogICAgICAgICAgICAKICAgICAgICAgICAgc2VsZi5zZW5kX2hlYWRlcignWC1Db250ZW50LVR5cGUtT3B0aW9ucycsICdub3NuaWZmJykKICAgICAgICAKICAgICAgICBzdXBlcigpLmVuZF9oZWFkZXJzKCkKICAgIAogICAgZGVmIGRvX0dFVChzZWxmKToKICAgICAgICBwYXRoID0gc2VsZi50cmFuc2xhdGVfcGF0aChzZWxmLnBhdGgpCiAgICAgICAgaWYgb3MucGF0aC5leGlzdHMocGF0aCkgYW5kIG5vdCBvcy5wYXRoLmlzZGlyKHBhdGgpOgogICAgICAgICAgICBpZl9ub25lX21hdGNoID0gc2VsZi5oZWFkZXJzLmdldCgnSWYtTm9uZS1NYXRjaCcpCiAgICAgICAgICAgIGlmIGlmX25vbmVfbWF0Y2g6CiAgICAgICAgICAgICAgICB0cnk6CiAgICAgICAgICAgICAgICAgICAgc3RhdCA9IG9zLnN0YXQocGF0aCkKICAgICAgICAgICAgICAgICAgICBldGFnID0gZicie2ludChzdGF0LnN0X210aW1lKX0te3N0YXQuc3Rfc2l6ZX0iJwogICAgICAgICAgICAgICAgICAgIGlmIGlmX25vbmVfbWF0Y2ggPT0gZXRhZzoKICAgICAgICAgICAgICAgICAgICAgICAgc2VsZi5zZW5kX3Jlc3BvbnNlKDMwNCkKICAgICAgICAgICAgICAgICAgICAgICAgc2VsZi5zZW5kX2hlYWRlcignRVRhZycsIGV0YWcpCiAgICAgICAgICAgICAgICAgICAgICAgIHNlbGYuZW5kX2hlYWRlcnMoKQogICAgICAgICAgICAgICAgICAgICAgICByZXR1cm4KICAgICAgICAgICAgICAgIGV4Y2VwdDogcGFzcwogICAgICAgIAogICAgICAgIHN1cGVyKCkuZG9fR0VUKCkKCndpdGggc29ja2V0c2VydmVyLlRDUFNlcnZlcigoIiIsIFBPUlQpLCBDYWNoaW5nSGFuZGxlcikgYXMgaHR0cGQ6CiAgICBwcmludChmIkRlQ2xvdWQgV2VsY29tZSBTZXJ2ZXIgb24gcG9ydCB7UE9SVH0iKQogICAgaHR0cGQuc2VydmVfZm9yZXZlcigp";

    // general/assets/index.html  (4165 bytes)
    private const string GeneralIndexHtmlSha256 = "0f82d3dc3554ff8fd1a38640f5d3667f124e2b0ac398e5fa71a48ebe7fe9ac4d";
    private const string GeneralIndexHtmlDataUri = "data:text/html;base64,PCFET0NUWVBFIGh0bWw+CjxodG1sPgo8aGVhZD4KICAgIDx0aXRsZT5fX1ZNX05BTUVfXzwvdGl0bGU+CiAgICA8bWV0YSBuYW1lPSJ2aWV3cG9ydCIgY29udGVudD0id2lkdGg9ZGV2aWNlLXdpZHRoLGluaXRpYWwtc2NhbGU9MSI+CiAgICA8bGluayByZWw9InByZWNvbm5lY3QiIGhyZWY9Imh0dHBzOi8vZm9udHMuZ29vZ2xlYXBpcy5jb20iPgogICAgPGxpbmsgcmVsPSJwcmVjb25uZWN0IiBocmVmPSJodHRwczovL2ZvbnRzLmdzdGF0aWMuY29tIiBjcm9zc29yaWdpbj4KICAgIDxsaW5rIGhyZWY9Imh0dHBzOi8vZm9udHMuZ29vZ2xlYXBpcy5jb20vY3NzMj9mYW1pbHk9SmV0QnJhaW5zK01vbm86d2dodEA0MDA7NTAwOzYwMCZmYW1pbHk9T3V0Zml0OndnaHRAMzAwOzQwMDs1MDA7NjAwOzcwMCZkaXNwbGF5PXN3YXAiIHJlbD0ic3R5bGVzaGVldCI+CiAgICA8c3R5bGU+CiAgICAgICAgKiB7CiAgICAgICAgICAgIG1hcmdpbjogMDsKICAgICAgICAgICAgcGFkZGluZzogMDsKICAgICAgICAgICAgYm94LXNpemluZzogYm9yZGVyLWJveDsKICAgICAgICB9CgogICAgICAgIGJvZHkgewogICAgICAgICAgICBmb250LWZhbWlseTogJ091dGZpdCcsIC1hcHBsZS1zeXN0ZW0sIEJsaW5rTWFjU3lzdGVtRm9udCwgc2Fucy1zZXJpZjsKICAgICAgICAgICAgbWluLWhlaWdodDogMTAwdmg7CiAgICAgICAgICAgIGRpc3BsYXk6IGZsZXg7CiAgICAgICAgICAgIGp1c3RpZnktY29udGVudDogY2VudGVyOwogICAgICAgICAgICBhbGlnbi1pdGVtczogY2VudGVyOwogICAgICAgICAgICBiYWNrZ3JvdW5kOiAjMGEwYjBmOwogICAgICAgICAgICBjb2xvcjogI2YwZjJmNTsKICAgICAgICAgICAgcG9zaXRpb246IHJlbGF0aXZlOwogICAgICAgICAgICBvdmVyZmxvdzogaGlkZGVuOwogICAgICAgIH0KCiAgICAgICAgLyogQmFja2dyb3VuZCBFZmZlY3RzICovCiAgICAgICAgYm9keTo6YmVmb3JlIHsKICAgICAgICAgICAgY29udGVudDogJyc7CiAgICAgICAgICAgIHBvc2l0aW9uOiBmaXhlZDsKICAgICAgICAgICAgaW5zZXQ6IDA7CiAgICAgICAgICAgIGJhY2tncm91bmQtaW1hZ2U6IGxpbmVhci1ncmFkaWVudChyZ2JhKDI1NSwyNTUsMjU1LDAuMDIpIDFweCwgdHJhbnNwYXJlbnQgMXB4KSwgCiAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIGxpbmVhci1ncmFkaWVudCg5MGRlZywgcmdiYSgyNTUsMjU1LDI1NSwwLjAyKSAxcHgsIHRyYW5zcGFyZW50IDFweCk7CiAgICAgICAgICAgIGJhY2tncm91bmQtc2l6ZTogNjBweCA2MHB4OwogICAgICAgICAgICBwb2ludGVyLWV2ZW50czogbm9uZTsKICAgICAgICAgICAgei1pbmRleDogMDsKICAgICAgICB9CgogICAgICAgIGJvZHk6OmFmdGVyIHsKICAgICAgICAgICAgY29udGVudDogJyc7CiAgICAgICAgICAgIHBvc2l0aW9uOiBmaXhlZDsKICAgICAgICAgICAgd2lkdGg6IDYwMHB4OwogICAgICAgICAgICBoZWlnaHQ6IDYwMHB4OwogICAgICAgICAgICBib3JkZXItcmFkaXVzOiA1MCU7CiAgICAgICAgICAgIGJhY2tncm91bmQ6ICMwMGQ0YWE7CiAgICAgICAgICAgIGZpbHRlcjogYmx1cigxMjBweCk7CiAgICAgICAgICAgIG9wYWNpdHk6IDAuMTU7CiAgICAgICAgICAgIHRvcDogLTIwMHB4OwogICAgICAgICAgICByaWdodDogLTEwMHB4OwogICAgICAgICAgICBwb2ludGVyLWV2ZW50czogbm9uZTsKICAgICAgICAgICAgei1pbmRleDogMDsKICAgICAgICB9CgogICAgICAgIC5jYXJkIHsKICAgICAgICAgICAgYmFja2dyb3VuZDogIzEyMTQxYTsKICAgICAgICAgICAgcGFkZGluZzogM3JlbTsKICAgICAgICAgICAgYm9yZGVyLXJhZGl1czogMjRweDsKICAgICAgICAgICAgdGV4dC1hbGlnbjogY2VudGVyOwogICAgICAgICAgICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDI1NSwgMjU1LCAyNTUsIDAuMSk7CiAgICAgICAgICAgIG1heC13aWR0aDogNTAwcHg7CiAgICAgICAgICAgIHBvc2l0aW9uOiByZWxhdGl2ZTsKICAgICAgICAgICAgei1pbmRleDogMTsKICAgICAgICAgICAgYm94LXNoYWRvdzogMCAyMHB4IDYwcHggcmdiYSgwLCAwLCAwLCAwLjUpOwogICAgICAgIH0KCiAgICAgICAgaDEgewogICAgICAgICAgICBmb250LXNpemU6IDJyZW07CiAgICAgICAgICAgIG1hcmdpbi1ib3R0b206IDAuNXJlbTsKICAgICAgICAgICAgZm9udC13ZWlnaHQ6IDcwMDsKICAgICAgICB9CgogICAgICAgIC5uYW1lIHsKICAgICAgICAgICAgY29sb3I6ICMwMGQ0YWE7CiAgICAgICAgICAgIGZvbnQtZmFtaWx5OiAnSmV0QnJhaW5zIE1vbm8nLCBtb25vc3BhY2U7CiAgICAgICAgICAgIGZvbnQtd2VpZ2h0OiA2MDA7CiAgICAgICAgfQoKICAgICAgICBwIHsKICAgICAgICAgICAgY29sb3I6ICM5Y2EzYWY7CiAgICAgICAgICAgIG1hcmdpbi10b3A6IDAuNXJlbTsKICAgICAgICB9CgogICAgICAgIC5zdGF0dXMgewogICAgICAgICAgICBkaXNwbGF5OiBpbmxpbmUtYmxvY2s7CiAgICAgICAgICAgIHBhZGRpbmc6IDAuNHJlbSAxcmVtOwogICAgICAgICAgICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDIxMiwgMTcwLCAwLjEpOwogICAgICAgICAgICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDAsIDIxMiwgMTcwLCAwLjMpOwogICAgICAgICAgICBib3JkZXItcmFkaXVzOiA5OTk5cHg7CiAgICAgICAgICAgIG1hcmdpbi10b3A6IDFyZW07CiAgICAgICAgICAgIGZvbnQtc2l6ZTogMC44NzVyZW07CiAgICAgICAgICAgIGNvbG9yOiAjMDBkNGFhOwogICAgICAgIH0KCiAgICAgICAgLmRvdCB7CiAgICAgICAgICAgIGRpc3BsYXk6IGlubGluZS1ibG9jazsKICAgICAgICAgICAgd2lkdGg6IDhweDsKICAgICAgICAgICAgaGVpZ2h0OiA4cHg7CiAgICAgICAgICAgIGJhY2tncm91bmQ6ICMwMGQ0YWE7CiAgICAgICAgICAgIGJvcmRlci1yYWRpdXM6IDUwJTsKICAgICAgICAgICAgbWFyZ2luLXJpZ2h0OiAwLjVyZW07CiAgICAgICAgICAgIGFuaW1hdGlvbjogcHVsc2UgMnMgaW5maW5pdGU7CiAgICAgICAgICAgIGJveC1zaGFkb3c6IDAgMCA4cHggcmdiYSgwLCAyMTIsIDE3MCwgMC41KTsKICAgICAgICB9CgogICAgICAgIEBrZXlmcmFtZXMgcHVsc2UgewogICAgICAgICAgICAwJSwgMTAwJSB7CiAgICAgICAgICAgICAgICBvcGFjaXR5OiAxOwogICAgICAgICAgICB9CgogICAgICAgICAgICA1MCUgewogICAgICAgICAgICAgICAgb3BhY2l0eTogMC41OwogICAgICAgICAgICB9CiAgICAgICAgfQoKICAgICAgICAubm90ZSB7CiAgICAgICAgICAgIG1hcmdpbi10b3A6IDJyZW07CiAgICAgICAgICAgIHBhZGRpbmc6IDFyZW07CiAgICAgICAgICAgIGJhY2tncm91bmQ6ICMxYTFkMjY7CiAgICAgICAgICAgIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMjU1LCAyNTUsIDI1NSwgMC4wNik7CiAgICAgICAgICAgIGJvcmRlci1yYWRpdXM6IDEwcHg7CiAgICAgICAgICAgIGZvbnQtc2l6ZTogMC44cmVtOwogICAgICAgICAgICBjb2xvcjogIzZiNzI4MDsKICAgICAgICB9CgogICAgICAgIGNvZGUgewogICAgICAgICAgICBiYWNrZ3JvdW5kOiAjMGEwYjBmOwogICAgICAgICAgICBwYWRkaW5nOiAwLjJyZW0gMC40cmVtOwogICAgICAgICAgICBib3JkZXItcmFkaXVzOiA2cHg7CiAgICAgICAgICAgIGZvbnQtc2l6ZTogMC43NXJlbTsKICAgICAgICAgICAgY29sb3I6ICNmNTllMGI7CiAgICAgICAgICAgIGZvbnQtZmFtaWx5OiAnSmV0QnJhaW5zIE1vbm8nLCBtb25vc3BhY2U7CiAgICAgICAgfQogICAgPC9zdHlsZT4KPC9oZWFkPgo8Ym9keT4KICAgIDxkaXYgY2xhc3M9ImNhcmQiPgogICAgICAgIDxoMT5WaXJ0dWFsIE1hY2hpbmUgPHNwYW4gY2xhc3M9Im5hbWUiPl9fVk1fTkFNRV9fPC9zcGFuPjwvaDE+CiAgICAgICAgPHA+RGVjZW50cmFsaXplZCBjb21wdXRlLCBydW5uaW5nIG9uIHlvdXIgdGVybXMuPC9wPgogICAgICAgIDxkaXYgY2xhc3M9InN0YXR1cyI+CiAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJkb3QiPjwvc3Bhbj5PbmxpbmUKICAgICAgICA8L2Rpdj4KICAgICAgICA8ZGl2IGNsYXNzPSJub3RlIj4KICAgICAgICAgICAgVG8gcmVtb3ZlIHRoaXMgcGFnZSBhbmQgZnJlZSBwb3J0IDgwOjxicj4KICAgICAgICAgICAgPGNvZGU+c3VkbyBzeXN0ZW1jdGwgZGlzYWJsZSAtLW5vdyB3ZWxjb21lPC9jb2RlPgogICAgICAgIDwvZGl2PgogICAgPC9kaXY+CjwvYm9keT4KPC9odG1sPg==";


    // ── Summary ─────────────────────────────────────────────────────────────────
    // Generated: 2026-05-04T15:48:55Z
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
