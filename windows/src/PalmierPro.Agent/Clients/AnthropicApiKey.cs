namespace PalmierPro.Agent.Clients;

/// <summary>API key lookup: env ANTHROPIC_API_KEY, then %LOCALAPPDATA%/PalmierPro/anthropic.key.</summary>
public static class AnthropicApiKey
{
    public static event Action? Changed;

    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PalmierPro", "anthropic.key");

    public static string? Load()
        => AgentApiKey.Load(AgentProvider.Anthropic);

    public static void Save(string key)
    {
        AgentApiKey.Save(AgentProvider.Anthropic, key);
        Changed?.Invoke();
    }

    public static void Delete()
    {
        AgentApiKey.Delete(AgentProvider.Anthropic);
        Changed?.Invoke();
    }
}
