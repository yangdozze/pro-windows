namespace PalmierPro.Agent.Clients;

/// <summary>Direct AI providers supported by Palmier Pro on Windows.</summary>
public enum AgentProvider
{
    Anthropic,
    OpenAI,
}

public static class AgentProviderExtensions
{
    public static string DisplayName(this AgentProvider provider) => provider switch
    {
        AgentProvider.OpenAI => "OpenAI",
        _ => "Anthropic",
    };

    public static string EnvironmentVariable(this AgentProvider provider) => provider switch
    {
        AgentProvider.OpenAI => "OPENAI_API_KEY",
        _ => "ANTHROPIC_API_KEY",
    };

    public static string DefaultModel(this AgentProvider provider) => provider switch
    {
        AgentProvider.OpenAI => "gpt-5.6-terra",
        _ => "claude-sonnet-5",
    };

    public static AgentProvider Parse(string? value)
        => string.Equals(value?.Trim(), "openai", StringComparison.OrdinalIgnoreCase)
            ? AgentProvider.OpenAI
            : AgentProvider.Anthropic;
}

/// <summary>
/// API-key lookup used by the in-app Agent. Environment variables take precedence,
/// followed by the key saved from Settings under LocalAppData.
/// </summary>
public static class AgentApiKey
{
    public static event Action? Changed;

    public static string? Load(AgentProvider provider)
    {
        var env = Environment.GetEnvironmentVariable(provider.EnvironmentVariable())?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        try
        {
            var path = StorePath(provider);
            if (!File.Exists(path)) return null;
            var key = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AgentProvider provider, string key)
    {
        var path = StorePath(provider);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, key.Trim());
        Changed?.Invoke();
    }

    public static void Delete(AgentProvider provider)
    {
        try
        {
            var path = StorePath(provider);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort removal; the caller can still use an environment key.
        }
        Changed?.Invoke();
    }

    private static string StorePath(AgentProvider provider)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PalmierPro",
            provider == AgentProvider.OpenAI ? "openai.key" : "anthropic.key");
}
