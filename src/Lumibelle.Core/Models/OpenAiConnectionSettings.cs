namespace lumibelle.Models;

public enum OpenAiServerKind { Auto, Standard, Vllm, Strata }

public sealed record OpenAiConnectionSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "OpenAI-compatible server";
    public string Url { get; init; } = "http://127.0.0.1:8000/v1";
    public bool Enabled { get; init; } = true;
    public int Concurrency { get; init; } = 1;
    public OpenAiServerKind Kind { get; init; }
    public OpenAiServerKind? DetectedKind { get; init; }
    // Null uses the server's advertised capabilities; model names are never evidence.
    public bool? SupportsImages { get; init; }
    public long? ContextLength { get; init; }
    public bool HasApiKey { get; init; }
}

public sealed record OpenAiConnectionStatus(string Server, string Name, bool Success, string Message,
    OpenAiServerKind Kind = OpenAiServerKind.Standard);
