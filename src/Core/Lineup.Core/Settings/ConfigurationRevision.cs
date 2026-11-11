using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lineup.Core.Common.Guides;
using Lineup.Core.Common.Streaming;

namespace Lineup.Core.Settings;

/// <summary>Creates content revisions without changing persisted configuration schemas.</summary>
public static class ConfigurationRevision
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new SetConverter<PhysicalDeviceId>(),
            new SetConverter<VirtualDeviceId>(),
            new SetConverter<GuideSourceId>()
        }
    };

    /// <summary>Returns a revision of editable Core settings, excluding operational schedule timestamps.</summary>
    public static string ForSettings(LineupSettings settings) =>
        Create(settings, nameof(LineupSettings.NextAutoFetchTime), nameof(LineupSettings.LastAutoFetchTime));

    /// <summary>Returns a canonical content revision, preserving ordered collections and normalizing identifier sets.</summary>
    public static string Create<T>(T value, params string[] excludedRootProperties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, JsonSerializer.SerializeToElement(value, Options), excludedRootProperties);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    /// <summary>Rejects a stale editor revision; callers must check inside the store's serialized update callback.</summary>
    public static void RequireMatch(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new SettingsConflictException();
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, string[]? excludedProperties = null)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (excludedProperties?.Contains(property.Name, StringComparer.Ordinal) == true)
                {
                    continue;
                }
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                Write(writer, item);
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private sealed class SetConverter<T> : JsonConverter<HashSet<T>>
    {
        /// <inheritdoc />
        public override HashSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Configuration revisions are write-only.");

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, HashSet<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value.Select(item => JsonSerializer.SerializeToElement(item, options)).OrderBy(item => item.GetRawText(), StringComparer.Ordinal))
            {
                item.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
    }
}

/// <summary>Indicates that saved settings changed after an editor loaded its draft.</summary>
public sealed class SettingsConflictException : InvalidOperationException
{
    /// <summary>Initializes a stale-editor conflict without including configuration values.</summary>
    public SettingsConflictException() : base("Settings changed while this draft was open. Reload the saved settings before trying again.")
    {
    }
}
