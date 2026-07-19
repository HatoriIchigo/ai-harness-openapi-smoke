using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ai_harness_openapi_smoke;

/// <summary>
/// レスポンスボディを OpenAPI スキーマと突き合わせる、正常系向けの軽量な構造検証。
///
/// フル JSON Schema（<c>pattern</c>／<c>format</c>／<c>minimum</c> 等）は検証しない。
/// <c>type</c>・<c>required</c>・<c>properties</c>・<c>items</c> の再帰照合のみを行う（v1 のスコープ）。
/// <see cref="IOpenApiSchema"/> を直接歩くため、OpenAPI 3.0/3.1 のスキーマ方言差（<c>nullable</c> vs 型配列 等）を
/// JSON Schema へ変換する過程で吸収し損ねるリスクが無い。
/// </summary>
public static class SchemaValidator
{
    /// <summary>循環参照や過度なネストからの保護。</summary>
    private const int MaxDepth = 20;

    public static IReadOnlyList<string> Validate(IOpenApiSchema schema, JsonNode? instance)
    {
        var violations = new List<string>();
        Walk(schema, instance, "$", violations, 0);
        return violations;
    }

    private static void Walk(IOpenApiSchema schema, JsonNode? instance, string path, List<string> violations, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (instance is null)
        {
            if (schema.Type is { } nullType && !nullType.HasFlag(JsonSchemaType.Null))
            {
                violations.Add($"{path}: null は許容されない型（期待 {nullType}）");
            }
            return;
        }

        if (schema.Type is { } type)
        {
            var actual = JsonKind(instance);
            if (!TypeMatches(type, actual))
            {
                violations.Add($"{path}: 型不一致（期待 {type} / 実際 {actual}）");
                return; // 型が違えば構造チェックを続けても意味が無い
            }
        }

        if (instance is JsonObject obj)
        {
            foreach (var required in schema.Required ?? (IEnumerable<string>)[])
            {
                if (!obj.ContainsKey(required))
                {
                    violations.Add($"{path}: 必須プロパティ '{required}' が無い");
                }
            }
            if (schema.Properties is not null)
            {
                foreach (var (name, propSchema) in schema.Properties)
                {
                    if (obj.TryGetPropertyValue(name, out var propValue))
                    {
                        Walk(propSchema, propValue, $"{path}.{name}", violations, depth + 1);
                    }
                }
            }
        }
        else if (instance is JsonArray arr && schema.Items is { } itemSchema)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                Walk(itemSchema, arr[i], $"{path}[{i}]", violations, depth + 1);
            }
        }
    }

    private static string JsonKind(JsonNode node) => node switch
    {
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<string>(out _) => "string",
        JsonValue v when IsIntegerValue(v) => "integer",
        JsonValue => "number",
        _ => "null",
    };

    private static bool IsIntegerValue(JsonValue value) =>
        value.TryGetValue<long>(out _) && !value.ToJsonString().Contains('.');

    private static bool TypeMatches(JsonSchemaType type, string actualKind) => actualKind switch
    {
        "object" => type.HasFlag(JsonSchemaType.Object),
        "array" => type.HasFlag(JsonSchemaType.Array),
        "string" => type.HasFlag(JsonSchemaType.String),
        "boolean" => type.HasFlag(JsonSchemaType.Boolean),
        "integer" => type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number),
        "number" => type.HasFlag(JsonSchemaType.Number),
        "null" => type.HasFlag(JsonSchemaType.Null),
        _ => true,
    };
}
