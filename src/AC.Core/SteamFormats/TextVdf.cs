using System.Text;

namespace AC.Core.SteamFormats;

/// <summary>
/// Minimal text VDF parser (loginusers.vdf, libraryfolders.vdf).
/// Supports quoted strings, nested blocks and // comments.
/// </summary>
public sealed class VdfNode
{
    public string Name { get; init; } = "";
    public string? Value { get; init; }
    public List<VdfNode> Children { get; } = new();

    public VdfNode? this[string key] =>
        Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));

    public string Get(string key, string fallback = "") => this[key]?.Value ?? fallback;
}

public static class TextVdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode { Name = "<root>" };
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                var node = new VdfNode { Name = pendingKey ?? "" };
                stack.Peek().Children.Add(node);
                stack.Push(node);
                pendingKey = null;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                i++;
                continue;
            }
            string token = c == '"' ? ReadQuoted(text, ref i) : ReadBare(text, ref i);
            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Children.Add(new VdfNode { Name = pendingKey, Value = token });
                pendingKey = null;
            }
        }
        return root;
    }

    private static string ReadQuoted(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < s.Length && s[i] != '"')
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
            }
            else sb.Append(s[i]);
            i++;
        }
        i++; // closing quote
        return sb.ToString();
    }

    private static string ReadBare(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') i++;
        return s[start..i];
    }

    /// <summary>
    /// From loginusers.vdf: SteamID64 of the most recently logged-in user (MostRecent = 1),
    /// else the first one in the list.
    /// </summary>
    public static (ulong SteamId, string PersonaName)? MostRecentUser(string loginUsersVdf)
    {
        var users = Parse(loginUsersVdf)["users"];
        if (users is null || users.Children.Count == 0) return null;
        VdfNode? pick = users.Children.FirstOrDefault(u => u.Get("MostRecent") == "1") ?? users.Children[0];
        return ulong.TryParse(pick.Name, out var id) ? (id, pick.Get("PersonaName")) : null;
    }

    /// <summary>From libraryfolders.vdf: the folders of the Steam libraries.</summary>
    public static IReadOnlyList<string> LibraryPaths(string libraryFoldersVdf) =>
        Parse(libraryFoldersVdf)["libraryfolders"]?.Children.Select(f => f.Get("path")).Where(p => p.Length > 0).ToList() ?? [];

    /// <summary>From an appmanifest_*.acf: the app and its folder under steamapps\common.</summary>
    public static (uint AppId, string InstallDir)? AppManifest(string acf)
    {
        var state = Parse(acf)["AppState"];
        return state is not null && uint.TryParse(state.Get("appid"), out var id) && state.Get("installdir") is { Length: > 0 } dir
            ? (id, dir) : null;
    }
}
