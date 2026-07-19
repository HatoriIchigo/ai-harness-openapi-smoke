using System.Collections;
using System.Text.Json.Nodes;

namespace ai_harness_openapi_smoke;

/// <summary>
/// baselib の <see cref="ai_harness_baselib.PluginBase.Config"/>（YamlDotNet の既定デシリアライズ:
/// マップ＝<c>IDictionary</c>、リスト＝<c>IList</c>、スカラ＝<c>string</c>/<c>bool</c>/<c>int</c>/<c>double</c>）と、
/// OpenAPI 仕様側の <see cref="System.Text.Json.Nodes.JsonNode"/>（example・schema）を橋渡しする変換。
/// </summary>
public static class YamlJson
{
    /// <summary>YamlDotNet が返す汎用オブジェクトを <see cref="JsonNode"/> へ変換する。</summary>
    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        IDictionary dict => ToJsonObject(dict),
        string s => JsonValue.Create(s),
        IEnumerable list => ToJsonArray(list),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonObject ToJsonObject(IDictionary dict)
    {
        var obj = new JsonObject();
        foreach (DictionaryEntry entry in dict)
        {
            var key = entry.Key?.ToString();
            if (!string.IsNullOrEmpty(key))
            {
                obj[key] = ToJsonNode(entry.Value);
            }
        }
        return obj;
    }

    private static JsonArray ToJsonArray(IEnumerable list)
    {
        var arr = new JsonArray();
        foreach (var item in list)
        {
            arr.Add(ToJsonNode(item));
        }
        return arr;
    }

    /// <summary>
    /// リクエストの path/query/header パラメータへ埋め込む文字列表現を取り出す。
    /// オブジェクト・配列は複数値パラメータの表現が仕様依存で一意に決まらないため対象外（null）。
    /// </summary>
    public static string? AsParamString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
}
