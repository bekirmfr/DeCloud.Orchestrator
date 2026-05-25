using DeCloud.Shared.Models;
using Orchestrator.Models.Payment;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Resolves an operator's raw <see cref="NodePricing"/> against the
/// platform <see cref="PricingConfig"/> to produce the effective rates
/// that apply for billing and UI display. Honors the "0 = use platform
/// default" contract documented on <see cref="NodePricing"/> and clamps
/// every output to the corresponding floor rate.
///
/// One resolver, one resolution rule — used by every consumer that
/// needs effective pricing (billing, marketplace UI, /api/nodes/me/pricing).
/// </summary>
public static class PricingResolver
{
    public static NodePricing Resolve(NodePricing? raw, PricingConfig cfg)
    {
        raw ??= new NodePricing();
        return new NodePricing
        {
            Currency = string.IsNullOrEmpty(raw.Currency) ? "USDC" : raw.Currency,
            CpuPerHour = Math.Max(
                raw.CpuPerHour > 0 ? raw.CpuPerHour : cfg.DefaultCpuPerHour,
                cfg.FloorCpuPerHour),
            MemoryPerGbPerHour = Math.Max(
                raw.MemoryPerGbPerHour > 0 ? raw.MemoryPerGbPerHour : cfg.DefaultMemoryPerGbPerHour,
                cfg.FloorMemoryPerGbPerHour),
            StoragePerGbPerHour = Math.Max(
                raw.StoragePerGbPerHour > 0 ? raw.StoragePerGbPerHour : cfg.DefaultStoragePerGbPerHour,
                cfg.FloorStoragePerGbPerHour),
            GpuVramPerGbPerHour = Math.Max(
                raw.GpuVramPerGbPerHour > 0 ? raw.GpuVramPerGbPerHour : cfg.DefaultGpuVramPerGbPerHour,
                cfg.FloorGpuVramPerGbPerHour),
        };
    }
}