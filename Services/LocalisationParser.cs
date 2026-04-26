using System.Text.RegularExpressions;

namespace abilitydraft.Services;

public sealed partial class LocalisationParser
{
    public Dictionary<string, string> ParseTokens(IEnumerable<string> contents)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var content in contents)
        {
            foreach (Match match in TokenRegex().Matches(content))
            {
                tokens[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        return tokens;
    }

    [GeneratedRegex("\"([^\"]*)\"\\s+\"(.*)\"")]
    private static partial Regex TokenRegex();
}
