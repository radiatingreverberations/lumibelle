namespace lumibelle.Services.Projects;

/// <summary>
/// Default git files for a project folder outside the library, which people often keep under version control.
/// Written only where none exist, so a folder's own choices are never replaced.
/// </summary>
public static class ProjectGitFiles
{
    public const string Ignore = """
        # Written while Lumibelle has the project folder open
        .lumibelle.lock
        # Unfinished writes left by an interrupted save
        *.tmp

        """;

    // Media sizes and hashes are recorded, and generation inputs are stored by their hash: any line-ending
    // conversion on checkout would make them fail their checks.
    public const string Attributes = """
        # Store project files byte-for-byte; Lumibelle records hashes and sizes of its media.
        * -text

        """;

    public static void WriteDefaults(string folder)
    {
        foreach (var (name, content) in new[] { (".gitignore", Ignore), (".gitattributes", Attributes) })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) continue;
            try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); file.Write(System.Text.Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"))); }
            catch (IOException) when (File.Exists(path)) { }
        }
    }
}
