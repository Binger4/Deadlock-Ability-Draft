using System.Globalization;
using System.Text;

namespace abilitydraft.Services;

public sealed class Kv3Document
{
    public string Header { get; set; } = string.Empty;
    public Kv3Object Root { get; set; } = new();

    public Kv3Document Clone() => new()
    {
        Header = Header,
        Root = (Kv3Object)Root.Clone()
    };
}

public abstract class Kv3Value
{
    public abstract Kv3Value Clone();
}

public sealed class Kv3Object : Kv3Value
{
    private readonly Dictionary<string, Kv3Value> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public IEnumerable<string> Keys => _order;
    public IEnumerable<KeyValuePair<string, Kv3Value>> Pairs => _order.Select(key => new KeyValuePair<string, Kv3Value>(key, _values[key]));

    public Kv3Value this[string key]
    {
        get => _values[key];
        set => Set(key, value);
    }

    public bool TryGetValue(string key, out Kv3Value value) => _values.TryGetValue(key, out value!);
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool Remove(string key)
    {
        if (!_values.Remove(key))
        {
            return false;
        }

        _order.Remove(key);
        return true;
    }

    public void Set(string key, Kv3Value value)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }

        _values[key] = value;
    }

    public override Kv3Value Clone()
    {
        var clone = new Kv3Object();
        foreach (var (key, value) in Pairs)
        {
            clone.Set(key, value.Clone());
        }

        return clone;
    }
}

public sealed class Kv3Array : Kv3Value
{
    public List<Kv3Value> Items { get; } = [];

    public override Kv3Value Clone()
    {
        var clone = new Kv3Array();
        clone.Items.AddRange(Items.Select(item => item.Clone()));
        return clone;
    }
}

public sealed class Kv3Scalar(object? value) : Kv3Value
{
    public object? Value { get; set; } = value;
    public override Kv3Value Clone() => new Kv3Scalar(Value);
}

public sealed class Kv3TypedValue(string typeName, Kv3Value innerValue) : Kv3Value
{
    public string TypeName { get; set; } = typeName;
    public Kv3Value InnerValue { get; set; } = innerValue;
    public override Kv3Value Clone() => new Kv3TypedValue(TypeName, InnerValue.Clone());
}

public sealed class Kv3Parser
{
    private readonly string _text;
    private int _position;

    private Kv3Parser(string text)
    {
        _text = text;
    }

    public static Kv3Document Parse(string text)
    {
        var parser = new Kv3Parser(text);
        var doc = new Kv3Document();
        parser.SkipWhiteSpace();

        if (parser.PeekString("<!--"))
        {
            var end = text.IndexOf("-->", parser._position, StringComparison.Ordinal);
            if (end >= 0)
            {
                doc.Header = text[..(end + 3)].TrimEnd();
                parser._position = end + 3;
            }
        }

        parser.SkipWhiteSpaceAndComments();
        doc.Root = parser.ParseObject();
        return doc;
    }

    private Kv3Object ParseObject()
    {
        Expect('{');
        var obj = new Kv3Object();

        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (TryConsume('}'))
            {
                return obj;
            }

            var key = ReadToken();
            SkipWhiteSpaceAndComments();
            if (Peek() is '=' or ':')
            {
                _position++;
            }

            var value = ParseValue();
            obj.Set(key, value);
        }
    }

    private Kv3Value ParseValue()
    {
        SkipWhiteSpaceAndComments();
        var current = Peek();
        if (current == '{')
        {
            return ParseObject();
        }

        if (current == '[')
        {
            return ParseArray();
        }

        var token = ReadToken();
        SkipWhiteSpaceAndComments();
        if (TryConsume(':'))
        {
            return new Kv3TypedValue(token, ParseValue());
        }

        if (bool.TryParse(token, out var boolValue))
        {
            return new Kv3Scalar(boolValue);
        }

        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return new Kv3Scalar(intValue);
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return new Kv3Scalar(doubleValue);
        }

        if (string.Equals(token, "null", StringComparison.OrdinalIgnoreCase))
        {
            return new Kv3Scalar(null);
        }

        return new Kv3Scalar(token);
    }

    private Kv3Array ParseArray()
    {
        Expect('[');
        var array = new Kv3Array();

        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (TryConsume(']'))
            {
                return array;
            }

            array.Items.Add(ParseValue());
            SkipWhiteSpaceAndComments();
            TryConsume(',');
        }
    }

    private string ReadToken()
    {
        SkipWhiteSpaceAndComments();
        if (TryConsume('"'))
        {
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var ch = _text[_position++];
                if (ch == '"')
                {
                    return builder.ToString();
                }

                if (ch == '\\' && _position < _text.Length)
                {
                    var escaped = _text[_position++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => escaped
                    });
                }
                else
                {
                    builder.Append(ch);
                }
            }

            throw new FormatException("Unterminated string in KV3 document.");
        }

        var start = _position;
        while (_position < _text.Length)
        {
            var ch = _text[_position];
            if (char.IsWhiteSpace(ch) || ch is '{' or '}' or '[' or ']' or '=' or ':' or ',')
            {
                break;
            }

            _position++;
        }

        if (start == _position)
        {
            throw new FormatException($"Expected token at position {_position}.");
        }

        return _text[start.._position];
    }

    private void SkipWhiteSpace()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private void SkipWhiteSpaceAndComments()
    {
        while (true)
        {
            SkipWhiteSpace();
            if (PeekString("//"))
            {
                while (_position < _text.Length && _text[_position] is not '\r' and not '\n')
                {
                    _position++;
                }

                continue;
            }

            break;
        }
    }

    private char Peek() => _position < _text.Length ? _text[_position] : '\0';
    private bool PeekString(string value) => _position + value.Length <= _text.Length && _text.AsSpan(_position, value.Length).SequenceEqual(value);

    private bool TryConsume(char expected)
    {
        if (Peek() != expected)
        {
            return false;
        }

        _position++;
        return true;
    }

    private void Expect(char expected)
    {
        SkipWhiteSpaceAndComments();
        if (!TryConsume(expected))
        {
            throw new FormatException($"Expected '{expected}' at position {_position}.");
        }
    }
}

public static class Kv3Writer
{
    public static string Write(Kv3Document document)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(document.Header))
        {
            builder.AppendLine(document.Header);
        }

        WriteObject(builder, document.Root, 0);
        return builder.ToString();
    }

    private static void WriteObject(StringBuilder builder, Kv3Object obj, int depth)
    {
        builder.AppendLine("{");
        foreach (var (key, value) in obj.Pairs)
        {
            Indent(builder, depth + 1);
            builder.Append(EscapeKey(key));
            builder.Append(" = ");
            WriteValue(builder, value, depth + 1);
            builder.AppendLine();
        }

        Indent(builder, depth);
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, Kv3Array array, int depth)
    {
        builder.Append('[');
        if (array.Items.Count > 0)
        {
            builder.AppendLine();
            for (var i = 0; i < array.Items.Count; i++)
            {
                Indent(builder, depth + 1);
                WriteValue(builder, array.Items[i], depth + 1);
                if (i < array.Items.Count - 1)
                {
                    builder.Append(',');
                }

                builder.AppendLine();
            }

            Indent(builder, depth);
        }

        builder.Append(']');
    }

    private static void WriteValue(StringBuilder builder, Kv3Value value, int depth)
    {
        switch (value)
        {
            case Kv3Object obj:
                WriteObject(builder, obj, depth);
                break;
            case Kv3Array array:
                WriteArray(builder, array, depth);
                break;
            case Kv3Scalar scalar:
                builder.Append(FormatScalar(scalar.Value));
                break;
            case Kv3TypedValue typed:
                builder.Append(typed.TypeName);
                builder.Append(':');
                WriteValue(builder, typed.InnerValue, depth);
                break;
        }
    }

    private static string FormatScalar(object? value) => value switch
    {
        null => "null",
        bool boolValue => boolValue ? "true" : "false",
        long longValue => longValue.ToString(CultureInfo.InvariantCulture),
        int intValue => intValue.ToString(CultureInfo.InvariantCulture),
        double doubleValue => doubleValue.ToString(CultureInfo.InvariantCulture),
        float floatValue => floatValue.ToString(CultureInfo.InvariantCulture),
        _ => $"\"{EscapeString(value.ToString() ?? string.Empty)}\""
    };

    private static string EscapeKey(string key) => NeedsQuotes(key) ? $"\"{EscapeString(key)}\"" : key;
    private static bool NeedsQuotes(string value)
    {
        if (string.IsNullOrEmpty(value) || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return true;
        }

        return value.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_');
    }
    private static string EscapeString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    private static void Indent(StringBuilder builder, int depth) => builder.Append('\t', depth);
}
