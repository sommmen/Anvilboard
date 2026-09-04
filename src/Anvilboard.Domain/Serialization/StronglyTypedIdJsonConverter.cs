using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anvilboard.Domain.Serialization;

/// <summary>
/// Serializes every <see cref="IStronglyTypedId"/> struct (TeamId, IssueId, ...) as a bare GUID
/// string instead of the default <c>{ "value": "..." }</c> object. Shared by every host that
/// speaks JSON at its boundary (the ASP.NET Core API and the CLI/MCP agent surface) so a caller
/// never has to know these are wrapper structs rather than raw <see cref="Guid"/>s.
/// </summary>
public sealed class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsValueType && typeof(IStronglyTypedId).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(StronglyTypedIdJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class StronglyTypedIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IStronglyTypedId
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetGuid();
        return (TId)Activator.CreateInstance(typeof(TId), value)!;
    }

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
