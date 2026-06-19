using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakarovPhysicsSandbox.Dto;

namespace MakarovPhysicsSandbox;

// AOT-safe JSON: source-generated metadata for the whole scene DTO graph, plus a manual Vector3
// converter. System.Numerics.Vector3 exposes X/Y/Z as fields (not properties), which STJ skips by
// default, so it needs an explicit converter to round-trip correctly.
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an object for Vector3.");
        float x = 0f, y = 0f, z = 0f;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string? name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "X" or "x": x = reader.GetSingle(); break;
                case "Y" or "y": y = reader.GetSingle(); break;
                case "Z" or "z": z = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }
        return new Vector3(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(Vector3JsonConverter) })]
[JsonSerializable(typeof(SceneDto))]
internal partial class SceneJsonContext : JsonSerializerContext
{
}
