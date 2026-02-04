using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orchestrator.Services;

/// <summary>
/// Manages DNS records for Smart Port Allocation via Cloudflare API.
/// Creates A records pointing VM subdomains (*.direct.stackfi.tech) to node IPs.
/// </summary>
public class DirectAccessDnsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DirectAccessDnsService> _logger;

    private string? _cloudflareApiToken;
    private string? _cloudflareZoneId;
    private const string CloudflareApiBaseUrl = "https://api.cloudflare.com/client/v4";

    public DirectAccessDnsService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DirectAccessDnsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;

        // Load Cloudflare configuration
        _cloudflareApiToken = _configuration["Cloudflare:ApiToken"];
        _cloudflareZoneId = _configuration["Cloudflare:ZoneId"];

        if (string.IsNullOrEmpty(_cloudflareApiToken) || string.IsNullOrEmpty(_cloudflareZoneId))
        {
            _logger.LogWarning(
                "Cloudflare API credentials not configured. " +
                "Set Cloudflare:ApiToken and Cloudflare:ZoneId in appsettings.json to enable DNS management.");
        }
    }

    /// <summary>
    /// Create or update DNS A record for a VM
    /// </summary>
    public async Task<string?> CreateOrUpdateDnsRecordAsync(
        string dnsName,
        string targetIp,
        CancellationToken ct = default)
    {
        if (!IsConfigured())
        {
            _logger.LogWarning("Cloudflare not configured, skipping DNS record creation");
            return null;
        }

        try
        {
            _logger.LogInformation("Creating DNS record: {DnsName} → {TargetIp}", dnsName, targetIp);

            // Check if record already exists
            var existingRecordId = await FindExistingRecordAsync(dnsName, ct);
            if (existingRecordId != null)
            {
                _logger.LogInformation("Updating existing DNS record {RecordId}", existingRecordId);
                await UpdateDnsRecordAsync(existingRecordId, dnsName, targetIp, ct);
                return existingRecordId;
            }

            // Create new record
            var recordId = await CreateDnsRecordAsync(dnsName, targetIp, ct);
            _logger.LogInformation("✓ DNS record created: {DnsName} → {TargetIp} (ID: {RecordId})",
                dnsName, targetIp, recordId);

            return recordId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create/update DNS record for {DnsName}", dnsName);
            return null;
        }
    }

    /// <summary>
    /// Delete DNS record
    /// </summary>
    public async Task<bool> DeleteDnsRecordAsync(string recordId, CancellationToken ct = default)
    {
        if (!IsConfigured())
        {
            return false;
        }

        try
        {
            _logger.LogInformation("Deleting DNS record {RecordId}", recordId);

            var client = CreateHttpClient();
            var response = await client.DeleteAsync(
                $"{CloudflareApiBaseUrl}/zones/{_cloudflareZoneId}/dns_records/{recordId}",
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to delete DNS record {RecordId}: {Error}", recordId, error);
                return false;
            }

            _logger.LogInformation("✓ DNS record deleted: {RecordId}", recordId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting DNS record {RecordId}", recordId);
            return false;
        }
    }

    /// <summary>
    /// Create a new DNS A record
    /// </summary>
    private async Task<string> CreateDnsRecordAsync(
        string dnsName,
        string targetIp,
        CancellationToken ct)
    {
        var client = CreateHttpClient();

        var payload = new
        {
            type = "A",
            name = dnsName,
            content = targetIp,
            ttl = 120,  // 2 minutes for fast updates
            proxied = false  // Direct IP resolution, no Cloudflare proxy
        };

        var response = await client.PostAsJsonAsync(
            $"{CloudflareApiBaseUrl}/zones/{_cloudflareZoneId}/dns_records",
            payload,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"Cloudflare API error: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<CloudflareResponse>(ct);
        if (result == null || !result.Success || result.Result == null)
        {
            throw new Exception("Invalid response from Cloudflare API");
        }

        return result.Result.Id;
    }

    /// <summary>
    /// Update an existing DNS A record
    /// </summary>
    private async Task UpdateDnsRecordAsync(
        string recordId,
        string dnsName,
        string targetIp,
        CancellationToken ct)
    {
        var client = CreateHttpClient();

        var payload = new
        {
            type = "A",
            name = dnsName,
            content = targetIp,
            ttl = 120,
            proxied = false
        };

        var response = await client.PutAsJsonAsync(
            $"{CloudflareApiBaseUrl}/zones/{_cloudflareZoneId}/dns_records/{recordId}",
            payload,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"Cloudflare API error: {error}");
        }
    }

    /// <summary>
    /// Find existing DNS record by name
    /// </summary>
    private async Task<string?> FindExistingRecordAsync(string dnsName, CancellationToken ct)
    {
        try
        {
            var client = CreateHttpClient();
            var response = await client.GetAsync(
                $"{CloudflareApiBaseUrl}/zones/{_cloudflareZoneId}/dns_records?type=A&name={dnsName}",
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CloudflareListResponse>(ct);
            if (result == null || !result.Success || result.Result == null || result.Result.Count == 0)
            {
                return null;
            }

            return result.Result[0].Id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Create HTTP client with Cloudflare authentication
    /// </summary>
    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_cloudflareApiToken}");
        client.DefaultRequestHeaders.Add("User-Agent", "DeCloud-Orchestrator/1.0");
        return client;
    }

    /// <summary>
    /// Check if Cloudflare is configured
    /// </summary>
    private bool IsConfigured()
    {
        return !string.IsNullOrEmpty(_cloudflareApiToken) && !string.IsNullOrEmpty(_cloudflareZoneId);
    }

    // Cloudflare API response models
    private class CloudflareResponse
    {
        public bool Success { get; set; }
        public CloudflareDnsRecord? Result { get; set; }
        public List<CloudflareError>? Errors { get; set; }
    }

    private class CloudflareListResponse
    {
        public bool Success { get; set; }
        public List<CloudflareDnsRecord>? Result { get; set; }
        public List<CloudflareError>? Errors { get; set; }
    }

    private class CloudflareDnsRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public int Ttl { get; set; }
        public bool Proxied { get; set; }
    }

    private class CloudflareError
    {
        public int Code { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
