using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Swyf.CustomAI;

public static class GameProbe
{
    public static void ApplyInstalledSchema(JsonObject request, string configPath)
    {
        var game = Path.GetDirectoryName(Path.GetDirectoryName(configPath))!;
        var path = Path.Combine(game, "Scam With Your Friends_Data", "StreamingAssets", "kolkata-json", "core-prompts", "system-prompt.schema.json");
        if (!File.Exists(path)) return; // Older games use the bundled probe.
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 65536) throw new JsonException();
            var format = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException();
            if (format["type"]?.ToString() != "json_schema" || format["json_schema"]?["schema"] is not JsonObject schema || schema["type"]?.ToString() != "object") throw new JsonException();
            CheckSchema(schema, 0);
            JsonSchema.FromText(schema.ToJsonString());
            request["response_format"] = format;
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or InvalidOperationException)
        { throw new ApiFailure(400, "game_schema", "Could not read the installed game's response schema. Verify the game files before testing."); }
    }

    private static void CheckSchema(JsonNode? node, int depth)
    {
        if (depth > 24) throw new JsonException();
        if (node is JsonObject obj)
            foreach (var pair in obj)
            {
                if (pair.Key is "$ref" or "$dynamicRef" or "$recursiveRef" or "$id" or "pattern" or "patternProperties") throw new JsonException();
                CheckSchema(pair.Value, depth + 1);
            }
        else if (node is JsonArray array) foreach (var item in array) CheckSchema(item, depth + 1);
    }

    public static void ValidateResponse(JsonObject request, string text)
    {
        using var document = JsonDocument.Parse(text);
        CheckDuplicates(document.RootElement);
        var schema = request["response_format"]!["json_schema"]!["schema"]!;
        if (!JsonSchema.FromText(schema.ToJsonString()).Evaluate(JsonNode.Parse(text), new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid)
            throw new JsonException("Response does not match the installed game schema.");
    }

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>();
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
}
