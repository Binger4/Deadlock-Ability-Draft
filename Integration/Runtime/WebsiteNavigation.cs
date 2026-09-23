namespace AbilityDraft.Runtime;

public static class WebsiteNavigation
{
    public static string External(Uri origin, string value)
    {
        // Configured destinations are resolved by the website, never embedded in the VPK.
        if (value.StartsWith("project-links/", StringComparison.Ordinal) &&
            int.TryParse(value[14..], out var index) && index is >= 0 and <= 10 &&
            value == "project-links/" + index)
            return new Uri(origin, value).AbsoluteUri;
        if (value.StartsWith("presets/", StringComparison.Ordinal) && value.Length == 72 && value[8..].All(char.IsAsciiHexDigit))
            return new Uri(origin, value).AbsoluteUri;
        if (value is "https://github.com/Binger4" or "https://github.com/Binger4/Deadlock-Ability-Draft" or "https://discord.gg/SxQjYeA7aW") return value;
        throw new InvalidOperationException("Invalid external website link.");
    }
    public static Uri Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("WebsiteUrl must be an HTTPS origin (HTTP is allowed only on localhost).");
        return uri;
    }
    public static string Participant(Uri origin, string path)
    {
        if (!(path.StartsWith("room/", StringComparison.Ordinal) || path.StartsWith("create?gameEntry=", StringComparison.Ordinal) ||
              path.StartsWith("join?gameEntry=", StringComparison.Ordinal)) || path.Contains("..") || path.Contains('\\'))
            throw new InvalidOperationException("Invalid website participant path.");
        return new Uri(origin, path).AbsoluteUri;
    }
}
