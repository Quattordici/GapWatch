using System.Text;
using System.Text.Json;
using GapWatch.Core.Model;

namespace GapWatch.OpenApi;

/// <summary>
/// Maps JSON pointers (<c>/paths/~1pets/get</c>) to the place in the source text where that member or array item is
/// declared. For an object member that is the position of its key, which is what an editor should jump to.
/// </summary>
/// <remarks>
/// Pointers use the escaped form of RFC 6901 (<c>~0</c> for <c>~</c>, <c>~1</c> for <c>/</c>); the root is the empty string.
/// </remarks>
internal sealed class DocumentPositions
{
    private readonly Dictionary<string, SourcePosition> _positions = new(StringComparer.Ordinal);

    /// <summary>An index without positions, for documents that are not read from text.</summary>
    public static DocumentPositions None { get; } = new();

    public SourcePosition? this[string pointer] => _positions.GetValueOrDefault(pointer);

    public int Count => _positions.Count;

    public void Add(string pointer, SourcePosition position) => _positions.TryAdd(pointer, position);

    /// <summary>The pointer of a member of the object at <paramref name="parent"/>.</summary>
    public static string Child(string parent, string name) =>
        $"{parent}/{name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    /// <summary>The pointer of an item of the array at <paramref name="parent"/>.</summary>
    public static string Child(string parent, int index) => $"{parent}/{index}";

    /// <summary>
    /// Records the position of every member and array item in a UTF-8 JSON document. The document must already be known
    /// to be valid; the same reader options as for parsing are used, so comments and trailing commas are allowed.
    /// </summary>
    public static DocumentPositions FromJson(ReadOnlyMemory<byte> utf8, JsonReaderOptions options)
    {
        var result = new DocumentPositions();
        var lines = new LineMap(utf8.Span);
        var reader = new Utf8JsonReader(utf8.Span, options);

        // One frame per open container: its pointer, and for arrays the index of the next item.
        var frames = new Stack<(string Pointer, int NextIndex, bool IsArray)>();
        string? member = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    member = Child(frames.Peek().Pointer, reader.GetString()!);
                    result.Add(member, lines.Position(utf8.Span, (int)reader.TokenStartIndex));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.Pop();
                    break;

                case JsonTokenType.Comment:
                    break;

                default:
                    var pointer = Value((int)reader.TokenStartIndex);
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        frames.Push((pointer, 0, reader.TokenType == JsonTokenType.StartArray));
                    }

                    break;
            }
        }

        return result;

        // A value token: its pointer comes from the member name before it, or its index in the enclosing array.
        string Value(int offset)
        {
            if (frames.Count == 0)
            {
                result.Add(string.Empty, lines.Position(utf8.Span, offset));
                return string.Empty;
            }

            var (parent, index, isArray) = frames.Peek();
            if (!isArray)
            {
                return member!;
            }

            frames.Pop();
            frames.Push((parent, index + 1, true));
            var pointer = Child(parent, index);
            result.Add(pointer, lines.Position(utf8.Span, offset));
            return pointer;
        }
    }

    /// <summary>Line starts of a UTF-8 text, to turn byte offsets into 1-based line and UTF-16 column numbers.</summary>
    private sealed class LineMap
    {
        private readonly List<int> _starts = [0];

        public LineMap(ReadOnlySpan<byte> utf8)
        {
            for (var i = 0; i < utf8.Length; i++)
            {
                if (utf8[i] == (byte)'\n')
                {
                    _starts.Add(i + 1);
                }
            }
        }

        public SourcePosition Position(ReadOnlySpan<byte> utf8, int offset)
        {
            var line = _starts.BinarySearch(offset);
            if (line < 0)
            {
                line = ~line - 1;
            }

            var start = _starts[line];
            return new SourcePosition(line + 1, Encoding.UTF8.GetCharCount(utf8[start..offset]) + 1);
        }
    }
}
