namespace lumibelle.Models;

/// <summary>A stable pane destination; labels can change without losing layout preferences.</summary>
/// <param name="New">Items not seen yet, such as takes generated since the tab was last open.</param>
public sealed record StudioTab(string Id, string Label, int? Count = null, string? Issue = null, int New = 0);
