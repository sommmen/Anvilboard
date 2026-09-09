using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anvilboard.Domain.Serialization;

/// <summary>
/// Serializes <see cref="Role"/> and <see cref="Permission"/> as UPPER_SNAKE_CASE symbolic
/// strings (e.g. <c>"ADMINISTRATOR"</c>, <c>"READ_WRITE_ISSUES"</c>) as required by the
/// Workspace Authorization spec, while every other enum in the codebase keeps the default
/// System.Text.Json numeric representation.
/// </summary>
public sealed class UpperSnakeCaseEnumJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(Role) || typeToConvert == typeof(Permission);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(UpperSnakeCaseEnumJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class UpperSnakeCaseEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException($"Expected a string value for {typeof(TEnum).Name}.");
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(ToUpperSnakeCase(value.ToString()), text, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new JsonException($"'{text}' is not a valid {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToUpperSnakeCase(value.ToString()));

    private static string ToUpperSnakeCase(string pascalCase)
    {
        var builder = new StringBuilder(pascalCase.Length + 4);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}
