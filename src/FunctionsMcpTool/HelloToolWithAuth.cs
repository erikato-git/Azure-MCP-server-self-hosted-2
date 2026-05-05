using Azure.Identity;
using FunctionsMcpTool.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using static FunctionsMcpTool.ToolsInformation;

namespace FunctionsMcpTool;

public class HelloToolWithAuth(ILogger<HelloToolWithAuth> logger, CredentialBuilder credentialBuilder)
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    [Function(nameof(HelloToolWithAuth))]
    public async Task<string> Run(
        [McpToolTrigger(HelloToolWithAuthName, HelloToolWithAuthDescription)] ToolInvocationContext context)
    {
        logger.LogInformation("HelloTool invoked.");

        var credential = credentialBuilder.Build(context);

        using var graphClient = new GraphServiceClient(credential, GraphScopes);

        try
        {
            var me = await graphClient.Me.GetAsync().ConfigureAwait(false);
            return $"Hello, {me?.DisplayName} ({me?.Mail})!";
        }
        catch (ODataError ex)
        {
            logger.LogError(ex, "Graph API Error: {Code} - {Message}", ex.Error?.Code, ex.Error?.Message);
            throw new InvalidOperationException(
                $"Graph API error ({ex.Error?.Code}): {ex.Error?.Message}", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            logger.LogError(ex, "AuthenticationFailedException: {Message}", ex.Message);
            throw new InvalidOperationException(
                "Failed to authenticate with Microsoft Graph. Check your credentials and permissions.", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error: {Type} - {Message}", ex.GetType().FullName, ex.Message);
            throw;
        }
    }
}
