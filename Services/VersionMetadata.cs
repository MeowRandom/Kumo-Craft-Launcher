using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kumo.Services;

internal static class VersionMetadata
{
    public const string BaseVersion = "kumoBaseVersion";
    public const string LoaderVersion = "kumoLoaderVersion";
    public const string DisplayName = "kumoDisplayName";
    public const string Explicit = "kumoExplicit";
    public const string Dependency = "kumoDependency";
    public const string Modpack = "kumoModpack";

    public static string? GetString(JsonObject profile, string key) =>
        profile.Str(key);

    public static JsonObject? GetObject(JsonObject profile, string key) =>
        profile.Obj(key);

    public static bool GetBoolean(JsonObject profile, string key, bool fallback = false)
    {
        var value = profile[key];
        return value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : fallback;
    }

    public static void Set(JsonObject profile, string key, JsonNode? value)
    {
        profile[key] = value?.DeepClone();
    }

    public static void Set(JsonObject profile, string key, string value) =>
        Set(profile, key, JsonValue.Create(value));

    public static void Set(JsonObject profile, string key, bool value) =>
        Set(profile, key, JsonValue.Create(value));

    public static void Remove(JsonObject profile, string key)
    {
        profile.Remove(key);
    }

    public static bool IsOwnedKey(string key) =>
        key.StartsWith("kumo", StringComparison.OrdinalIgnoreCase);
}
