using System.Security.Cryptography;
using System.Text;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>
/// Whether an outside agent may reach this library over MCP. Off until turned on. <see cref="AllowClientApproval"/>
/// also lists the tools that approve a plan from the agent's conversation, where the client's prompt is the gate.
/// </summary>
public sealed record AgentAccessSettings(bool Enabled, bool AllowClientApproval, bool HasToken, long Revision);

public interface IAgentAccessStore
{
    Task<AgentAccessSettings> LoadAsync(CancellationToken ct = default);
    /// <summary>Turns access on or off. Turning it on the first time creates the token.</summary>
    Task<AgentAccessSettings> SaveAsync(bool enabled, bool allowClientApproval, long expectedRevision, CancellationToken ct = default);
    /// <summary>Replaces the token, so every client set up with the old one is refused.</summary>
    Task<AgentAccessSettings> RenewTokenAsync(long expectedRevision, CancellationToken ct = default);
    /// <summary>The token to give a client, or null before access was first turned on.</summary>
    Task<string?> ReadTokenAsync(CancellationToken ct = default);
    /// <summary>True when access is on and this is the token.</summary>
    Task<bool> AcceptsAsync(string? presented, CancellationToken ct = default);
    /// <summary>The saved switches, without reading the file on every request.</summary>
    Task<(bool Enabled, bool AllowClientApproval)> StateAsync(CancellationToken ct = default);
}

public sealed class FileAgentAccessStore(ApplicationPaths paths, ISecretProtector protector) : IAgentAccessStore
{
    private sealed record Stored(int SchemaVersion, long Revision, bool Enabled, bool AllowClientApproval, string? ProtectedToken);
    private readonly string _path = Path.Combine(paths.Data, "agent-access.json");
    // Asked on every request, so neither the file nor the key ring is read each time. A save clears it.
    private sealed record Current(bool Enabled, bool AllowClientApproval, byte[]? Token);
    private volatile Current? _current;

    public async Task<AgentAccessSettings> LoadAsync(CancellationToken ct = default) => Summary(await ReadAsync(ct));

    public async Task<AgentAccessSettings> SaveAsync(bool enabled, bool allowClientApproval, long expectedRevision, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var current = await ReadAsync(ct);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        return await WriteAsync(current with { Enabled = enabled, AllowClientApproval = enabled && allowClientApproval,
            ProtectedToken = current.ProtectedToken ?? (enabled ? NewToken() : null) }, ct);
    }

    public async Task<AgentAccessSettings> RenewTokenAsync(long expectedRevision, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var current = await ReadAsync(ct);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        return await WriteAsync(current with { ProtectedToken = NewToken() }, ct);
    }

    public async Task<string?> ReadTokenAsync(CancellationToken ct = default) =>
        (await CurrentAsync(ct)).Token is { } token ? Encoding.ASCII.GetString(token) : null;

    public async Task<bool> AcceptsAsync(string? presented, CancellationToken ct = default)
    {
        var current = await CurrentAsync(ct);
        return current.Enabled && current.Token is { } token && presented is { Length: > 0 and < 512 } &&
            CryptographicOperations.FixedTimeEquals(token, Encoding.UTF8.GetBytes(presented));
    }

    public async Task<(bool Enabled, bool AllowClientApproval)> StateAsync(CancellationToken ct = default)
    {
        var current = await CurrentAsync(ct);
        return (current.Enabled, current.AllowClientApproval);
    }

    private async Task<Current> CurrentAsync(CancellationToken ct)
    {
        if (_current is { } cached) return cached;
        var stored = await ReadAsync(ct);
        byte[]? token = null;
        if (stored.ProtectedToken is { } secret)
            try { token = Encoding.ASCII.GetBytes(protector.Unprotect(secret)); }
            catch (CryptographicException) { /* Saved under another account's key ring: refuse until the token is renewed. */ }
        var current = new Current(stored.Enabled, stored.AllowClientApproval, token);
        _current = current;
        return current;
    }

    private string NewToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try { return protector.Protect(token); }
        catch (CryptographicException) { throw new WorkspaceStoreException("Couldn’t protect the access token using this account’s key ring. Nothing was saved."); }
    }

    private async Task<AgentAccessSettings> WriteAsync(Stored next, CancellationToken ct)
    {
        next = next with { Revision = checked(next.Revision + 1) };
        Directory.CreateDirectory(paths.Data);
        await AtomicJsonFile.WriteAsync(_path, next, ct);
        _current = null;
        return Summary(next);
    }

    private async Task<Stored> ReadAsync(CancellationToken ct)
    {
        // A library not used yet has no data folder, which means the same as no saved settings.
        var stored = Directory.Exists(paths.Data) ? await AtomicJsonFile.ReadAsync<Stored>(_path, ct) ?? new Stored(1, 0, false, false, null) : new Stored(1, 0, false, false, null);
        return stored.SchemaVersion == 1 && stored.Revision >= 0 ? stored
            : throw new WorkspaceStoreException("The agent access settings are from a newer version of Lumibelle. Nothing was changed.");
    }

    private static AgentAccessSettings Summary(Stored s) => new(s.Enabled, s.AllowClientApproval, s.ProtectedToken is not null, s.Revision);
}
