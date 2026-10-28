namespace lumibelle.Models;

public sealed record ProjectInfo
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string VideoAspect { get; init; } = "16:9";
    public required DateTimeOffset CreatedUtc { get; init; }
}

public sealed record CreateProjectRequest(string Name, string? Description = null)
{
    public VisualStyle? VisualStyle { get; init; }
}

public sealed record UpdateProjectRequest(string Name, string? Description = null, string? VideoAspect = null);

public sealed record ProjectReadIssue(Guid ProjectId, string Message);

public sealed record ProjectLibrary(
    IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<ProjectReadIssue> Issues);
