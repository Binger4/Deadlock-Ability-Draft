using System.Text;
using System.Text.RegularExpressions;
using abilitydraft.Models;

namespace abilitydraft.Services;

public static partial class AbilityVariantLocalisation
{
    public const string ResourcePath = "resource/localization/citadel_heroes/";

    public static string AddAliases(string original, IEnumerable<string> abilityKeys)
    {
        var variants = abilityKeys.Distinct(StringComparer.Ordinal).Select(key => (Key: key, Source:
            key.StartsWith(DraftedHeroCompatibility.LivingInk + "_abilitydraft_", StringComparison.Ordinal) ? DraftedHeroCompatibility.LivingInk :
            key.StartsWith(DraftedHeroCompatibility.DrawInk + "_abilitydraft_", StringComparison.Ordinal) ? DraftedHeroCompatibility.DrawInk : null))
            .Where(v => v.Source is not null).ToArray();
        var aliases = new StringBuilder();
        var existing = TokenLine().Matches(original).Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (Match token in TokenLine().Matches(original))
        {
            var key = token.Groups["key"].Value;
            foreach (var variant in variants)
            {
                var source = variant.Source!;
                if (key != source && !key.StartsWith(source + "/", StringComparison.Ordinal) && !key.StartsWith(source + "_", StringComparison.Ordinal)) continue;
                // Already generated keys are not source tokens for another alias.
                if (key.StartsWith(source + "_abilitydraft_", StringComparison.Ordinal)) continue;
                var alias = variant.Key + key[source.Length..];
                if (existing.Add(alias)) aliases.Append("\t\t\"").Append(alias).Append("\" \"").Append(token.Groups["value"].Value).AppendLine("\"");
            }
        }
        if (aliases.Length == 0) return original;
        var rootEnd = original.LastIndexOf('}');
        var tokensEnd = rootEnd > 0 ? original.LastIndexOf('}', rootEnd - 1) : -1;
        if (tokensEnd < 0) throw new FormatException("Invalid hero localization file: missing Tokens block.");
        // Keep every original token and escaped value verbatim. A partial file
        // at this mounted path would hide the game's other hero translations.
        return original.Insert(tokensEnd, "\n" + aliases + "\t");
    }

    public static IEnumerable<GeneratedModFile> Generate(string templatesDirectory, IEnumerable<string> abilityKeys)
    {
        var directory = Path.Combine(templatesDirectory, ResourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Missing gameplay localization templates. Rebuild Integration/Build-BaseAddon.ps1 before exporting Living Ink.");
        var keys = abilityKeys.ToArray();
        foreach (var path in Directory.EnumerateFiles(directory, "citadel_heroes_*.txt").Order(StringComparer.Ordinal))
            yield return new(ResourcePath + Path.GetFileName(path), AddAliases(File.ReadAllText(path), keys));
    }

    [GeneratedRegex("(?m)^[\\t ]*\"(?<key>[^\"\\r\\n]+)\"[\\t ]+\"(?<value>(?:\\\\.|[^\"\\\\\\r\\n])*)\"")]
    private static partial Regex TokenLine();
}
