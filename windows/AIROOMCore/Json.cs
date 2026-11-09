using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

/// <summary>Executor and language serialize as their exact Swift rawValues
/// ("Claude Code", "zh-Hant", …); System.Text.Json ignores JsonPropertyName on
/// enum members, so these two get hand-written converters.</summary>
public sealed class ExecutorJsonConverter : JsonConverter<Executor>
{
    public override Executor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return ExecutorList.All.FirstOrDefault(executor => ExecutorName.Of(executor) == value, default);
    }

    public override void Write(Utf8JsonWriter writer, Executor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ExecutorName.Of(value));
}

public sealed class InterfaceLanguageJsonConverter : JsonConverter<InterfaceLanguage>
{
    public override InterfaceLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "zh-Hant" => InterfaceLanguage.ZhTW,
            "en" => InterfaceLanguage.English,
            _ => InterfaceLanguage.ZhTW,
        };

    public override void Write(Utf8JsonWriter writer, InterfaceLanguage value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            InterfaceLanguage.ZhTW => "zh-Hant",
            InterfaceLanguage.English => "en",
            _ => "zh-Hant",
        });
}

/// <summary>Shared JSON settings: camelCase keys and string enums, matching the
/// Swift Codable shapes this format was written against.</summary>
public static class CoreJson
{
    public static readonly JsonSerializerOptions Default = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Local state files stay human-readable: keep CJK unescaped (UTF-8).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                new ExecutorJsonConverter(),
                new InterfaceLanguageJsonConverter(),
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new MemberIDJsonConverter(),
            },
        };
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Default);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Default)
        ?? throw new JsonException("null is not a valid state document");
}
