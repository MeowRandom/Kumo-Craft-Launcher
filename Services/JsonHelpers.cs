using System.Text.Json.Nodes;

namespace Kumo.Services;

internal static class JsonHelpers
{
    public static string? Str(this JsonObject? obj, string name) =>
        obj?[name]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? obj[name]?.GetValue<string>()
            : null;

    public static bool Bool(this JsonObject? obj, string name, bool fallback = false) =>
        obj?[name]?.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? obj[name]!.GetValue<bool>()
            : fallback;

    public static JsonObject? Obj(this JsonObject? obj, string name) => obj?[name] as JsonObject;
    public static JsonArray? Arr(this JsonObject? obj, string name) => obj?[name] as JsonArray;

    public static string ToMavenPath(string coordinate)
    {
        var extension = "jar";
        var at = coordinate.LastIndexOf('@');
        if (at >= 0)
        {
            extension = coordinate[(at + 1)..];
            coordinate = coordinate[..at];
        }

        var parts = coordinate.Split(':');
        if (parts.Length < 3) return coordinate.Replace(':', '/') + "." + extension;
        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : string.Empty;
        return $"{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }
}
