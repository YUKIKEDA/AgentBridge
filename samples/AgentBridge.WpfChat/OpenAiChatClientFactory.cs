using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentBridge.WpfChat;

internal static class OpenAiChatClientFactory
{
    public const string ApiKeyVariable = "OPENAI_API_KEY";
    public const string ModelVariable = "OPENAI_MODEL";
    public const string EndpointVariable = "OPENAI_ENDPOINT";
    public const string DefaultModel = "gpt-4o-mini";

    public static string ResolveModel()
    {
        string? model = Environment.GetEnvironmentVariable(ModelVariable);
        return string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    public static IChatClient? TryCreate(out string? error)
    {
        string? apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            error = $"{ApiKeyVariable} が未設定です。手順は docs/samples.md を見てください。";
            return null;
        }

        OpenAIClientOptions options = new();
        string? endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }

        OpenAIClient client = new(new ApiKeyCredential(apiKey), options);
        error = null;
        return client.GetChatClient(ResolveModel()).AsIChatClient();
    }
}
