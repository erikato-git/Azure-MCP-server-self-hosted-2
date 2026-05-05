using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Hosting;

namespace FunctionsMcpTool.Helpers;

public class CredentialBuilder(IHostEnvironment hostEnvironment)
{
    internal TokenCredential Build(ToolInvocationContext context) // [SPEC-01]
    {
        if (hostEnvironment.IsDevelopment())
            return new ChainedTokenCredential( // [SPEC-01]
                new AzureCliCredential(),
                new VisualStudioCodeCredential(),
                new VisualStudioCredential(),
                new AzureDeveloperCliCredential());

        return BuildOnBehalfOf(context);
    }

    private static TokenCredential BuildOnBehalfOf(ToolInvocationContext context)
    {
        if (!context.TryGetHttpTransport(out var transport))
            throw new InvalidOperationException("No HTTP transport available. Is App Service Authentication enabled?");

        var userToken = GetUserToken(transport!);
        var tenantId = GetTenantId(transport!);
        var assertion = BuildClientAssertionCallback();

        var clientId = Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID")
            ?? throw new InvalidOperationException("WEBSITE_AUTH_CLIENT_ID is not set.");

        return new OnBehalfOfCredential(tenantId, clientId, assertion, userToken);
    }

    private static string GetUserToken(HttpTransport transport)
    {
        if (transport.Headers.TryGetValue("X-MS-TOKEN-AAD-ACCESS-TOKEN", out var token) && !string.IsNullOrEmpty(token))
            return token;

        if (transport.Headers.TryGetValue("Authorization", out var header) && header.StartsWith("Bearer "))
            return header["Bearer ".Length..];

        throw new InvalidOperationException("No access token found. Is App Service Authentication enabled with token store?");
    }

    private static string GetTenantId(HttpTransport transport)
    {
        if (!transport.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded))
            throw new InvalidOperationException("X-MS-CLIENT-PRINCIPAL header is missing.");

        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        using var doc = System.Text.Json.JsonDocument.Parse(decoded);

        if (doc.RootElement.TryGetProperty("claims", out var claims) &&
            claims.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var claim in claims.EnumerateArray())
            {
                if (claim.TryGetProperty("typ", out var typ) &&
                    (typ.GetString() == "tid" ||
                     typ.GetString() == "http://schemas.microsoft.com/identity/claims/tenantid"))
                {
                    return claim.GetProperty("val").GetString()
                        ?? throw new InvalidOperationException("Tenant ID claim is null.");
                }
            }
        }

        throw new InvalidOperationException("Could not find tenant ID in X-MS-CLIENT-PRINCIPAL.");
    }

    private static Func<CancellationToken, Task<string>> BuildClientAssertionCallback()
    {
        var federatedMiClientId = Environment.GetEnvironmentVariable("OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID")
            ?? throw new InvalidOperationException("OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID is not set.");

        var audience = Environment.GetEnvironmentVariable("TokenExchangeAudience") ?? "api://AzureADTokenExchange";
        var mi = new ManagedIdentityCredential(federatedMiClientId);

        return async ct => (await mi.GetTokenAsync(
            new TokenRequestContext([$"{audience}/.default"]), ct).ConfigureAwait(false)).Token;
    }
}
