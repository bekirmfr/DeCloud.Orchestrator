using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Orchestrator.Background;

namespace Orchestrator.Infrastructure;

/// <summary>
/// API Key authentication handler
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly IUserService _userService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IUserService userService)
        : base(options, logger, encoder)
    {
        _userService = userService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = apiKeyHeaderValues.FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        // Use GetUserByApiKeyAsync instead of ValidateApiKeyAsync
        var user = await _userService.GetUserByApiKeyAsync(apiKey);
        if (user == null)
        {
            return AuthenticateResult.Fail("Invalid API key");
        }

        // Update last used time for the API key
        var keyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(apiKey)));

        var apiKeyObj = user.ApiKeys.FirstOrDefault(k => k.KeyHash == keyHash);
        if (apiKeyObj != null)
        {
            apiKeyObj.LastUsedAt = DateTime.UtcNow;
            await _userService.UpdateUserAsync(user);
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim("wallet", user.WalletAddress),
            new Claim(ClaimTypes.Role, "user"),
            new Claim(ClaimTypes.AuthenticationMethod, "ApiKey")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

public static class ApiKeyAuthenticationExtensions
{
    public static AuthenticationBuilder AddApiKey(
        this AuthenticationBuilder builder,
        Action<ApiKeyAuthenticationOptions>? configureOptions = null)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            "ApiKey",
            configureOptions ?? (_ => { }));
    }
}