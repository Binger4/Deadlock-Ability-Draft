using System.Text.Json;
using System.Text.RegularExpressions;
using AbilityDraft.Contracts;

namespace AbilityDraft.Runtime;

// Deadworks v0.4.16's upstream UI channel passes through console tokenization.
// URI-component encoding preserves JSON quotes, pipes, semicolons and Unicode.
public static class PanoramaCommandCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static RoomCommand Decode(string encoded)
    {
        if (encoded.Length is 0 or > 6144 ||
            !Regex.IsMatch(encoded, "\\A[A-Za-z0-9_.!~*'()%\\-]+\\z"))
            throw new InvalidOperationException("Invalid integration command encoding.");
        try
        {
            var json = Uri.UnescapeDataString(encoded);
            if (json.Length > 2048) throw new InvalidOperationException("Integration command is too large.");
            return JsonSerializer.Deserialize<RoomCommand>(json, Json)
                ?? throw new InvalidOperationException("Empty integration command.");
        }
        catch (JsonException) { throw new InvalidOperationException("Malformed integration command."); }
    }
}
