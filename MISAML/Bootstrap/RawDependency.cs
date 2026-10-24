using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace MISAML.Bootstrap;

public class RawDependency
{
    public string? Version { get; set; }
    public string? Source { get; set; }

    // unknown keys, so loader can warn
    public List<string> UnknownKeys { get; } = [];
}

public class RawDependencyConverter : JsonConverter<RawDependency>
{
    public override RawDependency? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // a plain string is just the version, nothing else
        if (reader.TokenType == JsonTokenType.String)
            return new RawDependency { Version = reader.GetString() };

        // anything that isn't a string or an object is an error
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("a dependency must be a version string or an object");

        using var doc = JsonDocument.ParseValue(ref reader);
        
        var dependency = new RawDependency();

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // ignore case for key matching
            var key = property.Name.ToLowerInvariant();

            if (key == "version")
                dependency.Version = ReadString(property.Value);
            else if (key == "source")
                dependency.Source = ReadString(property.Value);
            else
                dependency.UnknownKeys.Add(property.Name);
                
        }

        return dependency;
    }

    // never write to mod.json
    public override void Write(Utf8JsonWriter writer, RawDependency value, JsonSerializerOptions options)
        => throw new NotSupportedException();

    // the value as txt, or null if not JSON string
    private static string? ReadString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}