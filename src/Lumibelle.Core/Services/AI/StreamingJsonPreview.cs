using System.Globalization;
using System.Text;
using System.Text.Json;

namespace lumibelle.Services.AI;

/// <summary>A display-only snapshot of a JSON prefix. Never use it to validate or apply a response.</summary>
public sealed record StreamingJsonValue(string? Name, JsonValueKind Kind, string? Text, bool Complete, IReadOnlyList<StreamingJsonValue> Children)
{
    public StreamingJsonValue? Property(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    public bool HasContent => Text is { Length: > 0 } || Children.Any(c => c.HasContent);
}

public sealed record StreamingJsonPreview(StreamingJsonValue? Root, bool Limited)
{
    public const int MaximumCharacters = 512 * 1024;
    private const int MaximumNodes = 4096;

    public static StreamingJsonPreview Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, false);
        var limited = raw.Length > MaximumCharacters;
        var text = raw.AsSpan(0, Math.Min(raw.Length, MaximumCharacters)).TrimStart();
        // Fences are presentation wrappers only. The original response remains untouched.
        if (text.StartsWith("```", StringComparison.Ordinal)) {
            var newline = text.IndexOf('\n');
            if (newline < 0) return new(null, limited);
            text = text[(newline + 1)..].TrimStart();
        }
        if (text.IsEmpty || text[0] is not ('{' or '[')) return new(null, limited);
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        var reader = new Utf8JsonReader(bytes, false, new(new JsonReaderOptions { MaxDepth = 32 }));
        var stack = new Stack<Node>();
        Node? root = null;
        var nodes = 0;
        void Add(Node node)
        {
            if (stack.TryPeek(out var parent)) {
                node.Name = parent.PendingName;
                parent.PendingName = null;
                parent.Children.Add(node);
            }
            else root = node;
        }
        try {
            while (reader.Read()) {
                if (++nodes > MaximumNodes) { limited = true; break; }
                switch (reader.TokenType) {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        var container = new Node(reader.TokenType == JsonTokenType.StartObject ? JsonValueKind.Object : JsonValueKind.Array);
                        Add(container); stack.Push(container); break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.Pop().Complete = true; break;
                    case JsonTokenType.PropertyName:
                        stack.Peek().PendingName = reader.GetString(); break;
                    case JsonTokenType.String:
                        Add(new(JsonValueKind.String) { Text = reader.GetString(), Complete = true }); break;
                    case JsonTokenType.Number:
                        Add(new(JsonValueKind.Number) { Text = Encoding.UTF8.GetString(reader.ValueSpan), Complete = true }); break;
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        Add(new(reader.TokenType == JsonTokenType.True ? JsonValueKind.True : JsonValueKind.False) { Text = reader.TokenType == JsonTokenType.True ? "Yes" : "No", Complete = true }); break;
                    case JsonTokenType.Null:
                        Add(new(JsonValueKind.Null) { Text = "None", Complete = true }); break;
                }
                if (root?.Complete == true) break;
            }
            // Utf8JsonReader intentionally leaves an unfinished string unread. Decode its
            // available characters for the current card, without inventing a closing token.
            if (nodes <= MaximumNodes && stack.TryPeek(out var current) && (current.Kind == JsonValueKind.Array || current.PendingName is not null)) {
                var tail = Encoding.UTF8.GetString(bytes.AsSpan((int)reader.BytesConsumed)).AsSpan().TrimStart();
                if (!tail.IsEmpty && tail[0] == ',') tail = tail[1..].TrimStart();
                if (!tail.IsEmpty && tail[0] == '"') Add(new(JsonValueKind.String) { Text = PartialString(tail) });
            }
        }
        catch (JsonException) { /* Show the readable prefix; final validation owns errors. */ }
        catch (InvalidOperationException) { /* For example, an invalid Unicode escape. */ }
        return new(root?.Freeze(), limited);
    }

    private static string PartialString(ReadOnlySpan<char> text)
    {
        var result = new StringBuilder();
        for (var i = 1; i < text.Length; i++) {
            var ch = text[i];
            if (ch == '"' || ch < ' ') break;
            if (ch != '\\') { result.Append(ch); continue; }
            if (++i == text.Length) break;
            ch = text[i];
            if (ch == 'u') {
                if (i + 4 >= text.Length || !ushort.TryParse(text.Slice(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) break;
                result.Append((char)code); i += 4;
            }
            else {
                var escaped = ch switch { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => '\0' };
                if (escaped == '\0') break;
                result.Append(escaped);
            }
        }
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result.Length--;
        return result.ToString();
    }

    private sealed class Node(JsonValueKind kind)
    {
        public string? Name, Text, PendingName;
        public bool Complete;
        public JsonValueKind Kind = kind;
        public List<Node> Children = [];
        public StreamingJsonValue Freeze() => new(Name, Kind, Text, Complete, Children.Select(c => c.Freeze()).ToArray());
    }
}
