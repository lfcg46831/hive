using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Hive.Infrastructure.Ai;

internal static class RealAiChatClientFactory
{
    internal static IChatClient Create(RealAiGatewayProviderSettings settings, string modelId) =>
        Create(settings, modelId, new HttpClientHandler { AllowAutoRedirect = false });

    // The injected handler lets contract tests exercise the actual SDK serialization without a network.
    internal static IChatClient Create(
        RealAiGatewayProviderSettings settings, string modelId, HttpMessageHandler handler)
    {
        var isMistral = settings.DefaultProvider.ProviderId == "mistral";
        var httpClient = new HttpClient(isMistral ? new MistralRequestHandler(handler) : handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = new OpenAIClientOptions
        {
            Endpoint = settings.Endpoint ?? new Uri(isMistral
                ? "https://api.mistral.ai/v1" : "https://api.openai.com/v1"),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        var client = new ChatClient(modelId, new ApiKeyCredential(settings.ApiKey), options).AsIChatClient();
        return new OwnedChatClient(client, httpClient);
    }

    private sealed class OwnedChatClient(IChatClient inner, HttpClient transport) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) transport.Dispose();
        }
    }

    private sealed class MistralRequestHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is { } content)
            {
                var json = JsonNode.Parse(await content.ReadAsStringAsync(cancellationToken))!.AsObject();
                if (json.Remove("max_completion_tokens", out var maxTokens))
                {
                    json["max_tokens"] = maxTokens;
                    request.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
                    content.Dispose();
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
