namespace lumibelle.Services.AI;

// A JSON string may legitimately contain spaces, escaped quotes and line breaks.
// Only a long run of separator whitespace outside strings indicates this loop.
internal sealed class JsonOutputProgress
{
    internal const int MaximumSeparatorWhitespace = 512;
    private bool _inString, _escaped;
    private int _whitespace;

    public bool Observe(string? text)
    {
        foreach (var ch in text ?? string.Empty)
        {
            if (_inString)
            {
                if (_escaped) _escaped = false;
                else if (ch == '\\') _escaped = true;
                else if (ch == '"') _inString = false;
                _whitespace = 0;
            }
            else if (ch == '"') { _inString = true; _whitespace = 0; }
            else if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (++_whitespace >= MaximumSeparatorWhitespace) return false;
            }
            else _whitespace = 0;
        }
        return true;
    }
}
