using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.AI.Abstractions.Configuration;
using HomeManagement.AI.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.AI.Ollama;

public sealed class OllamaModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Ai.Ollama";

    public void Register(IServiceCollection services)
    {
        services.AddOptions<AiOptions>();

        services.AddHttpClient<OllamaLlmClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AiOptions>>().CurrentValue;
            var baseUrl = options.Ollama.BaseUrl.TrimEnd('/') + "/";
            client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.Ollama.TimeoutSeconds);
        });

        services.AddSingleton<ILLMClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AiOptions>>().CurrentValue;
            if (!options.Enabled)
            {
                return new DisabledLlmClient();
            }

            if (!options.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return new DisabledLlmClient();
            }

            return sp.GetRequiredService<OllamaLlmClient>();
        });
    }
}
