using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lineup.HDHomeRun.Device.Json;

/// <summary>
/// JSON converter that converts between boolean values and int/null representation
/// True is serialized as 1, False is serialized as null
/// 1 or any non-zero number is deserialized as true, null or 0 is deserialized as false
/// </summary>
internal class BoolToIntOrNullConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return false;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt32() == 1;
        }

        return false;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        if (value)
        {
            writer.WriteNumberValue(1);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
