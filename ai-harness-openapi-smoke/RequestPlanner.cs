using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ai_harness_openapi_smoke;

/// <summary>1 operation 分の実行済みリクエスト計画。<see cref="PathTemplate"/> はプレースホルダ埋め込み前の仕様パス。</summary>
public sealed record RequestPlan(
    HttpMethod Method,
    string PathTemplate,
    IReadOnlyDictionary<string, string> PathParams,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    JsonNode? Body,
    int ExpectedStatus);

/// <summary>
/// OpenAPI の 1 operation から、実際に投げるリクエストを組み立てる。
/// 値の優先順位は override（<see cref="OverrideEntry"/>） &gt; 仕様の example &gt; スキーマの example。
/// 必須パラメータ／必須リクエストボディ／2xx レスポンス定義／<c>security</c>（認証要件）のいずれかが
/// 解決できない operation は値を合成せず、呼び出し側で失敗（NG）として扱う（戻り値の <c>Error</c> に理由を返す）。
/// 認証情報そのものは合成しない（<c>headers</c>／<c>overrides[].headers</c> 等で用意されているかを見るのみ）。
/// </summary>
public static class RequestPlanner
{
    public static (RequestPlan? Plan, string? Error) Build(
        string pathKey,
        HttpMethod method,
        IEnumerable<IOpenApiParameter> pathItemParameters,
        OpenApiOperation operation,
        IReadOnlyList<OpenApiSecurityRequirement> documentSecurity,
        OverrideEntry? overrideEntry,
        IReadOnlyDictionary<string, string> defaultHeaders,
        IReadOnlyDictionary<string, string>? defaultQuery = null)
    {
        var pathParams = new Dictionary<string, string>();
        var query = new Dictionary<string, string>(defaultQuery ?? new Dictionary<string, string>());
        var headers = new Dictionary<string, string>(defaultHeaders, StringComparer.OrdinalIgnoreCase);

        // operation 側のパラメータが同名の path アイテム側を上書きする。
        var merged = new Dictionary<string, IOpenApiParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pathItemParameters)
        {
            merged[ParamKey(p)] = p;
        }
        foreach (var p in operation.Parameters ?? [])
        {
            merged[ParamKey(p)] = p;
        }

        foreach (var param in merged.Values)
        {
            if (string.IsNullOrEmpty(param.Name))
            {
                continue; // 名前の無いパラメータは解決しようがない。
            }

            var value = ResolveParamValue(param, overrideEntry);
            if (value is null)
            {
                if (param.Required)
                {
                    return (null, $"必須パラメータ '{param.Name}' ({param.In}) に example／override が無い");
                }
                continue;
            }

            switch (param.In)
            {
                case ParameterLocation.Path:
                    pathParams[param.Name] = value;
                    break;
                case ParameterLocation.Query:
                    query[param.Name] = value;
                    break;
                case ParameterLocation.Header:
                    headers[param.Name] = value;
                    break;
                default:
                    // Cookie／QueryString はこの harness の対象外（正常系の疎通確認には通常不要）。
                    break;
            }
        }

        // override 側で明示された path_params/query/headers は example 解決結果を上書きする。
        if (overrideEntry is not null)
        {
            foreach (var (k, v) in overrideEntry.PathParams)
            {
                pathParams[k] = v;
            }
            foreach (var (k, v) in overrideEntry.Query)
            {
                query[k] = v;
            }
            foreach (var (k, v) in overrideEntry.Headers)
            {
                headers[k] = v;
            }
        }

        // operation.Security が非 null なら document 側の既定を完全に上書きする（null は「未指定＝継承」、
        // 空配列は「明示的に認証不要」で SecurityResolver.Check がそのまま満たす）。
        var effectiveSecurity = (operation.Security as IReadOnlyList<OpenApiSecurityRequirement>) ?? documentSecurity;
        var (securitySatisfied, missingSecurity) = SecurityResolver.Check(effectiveSecurity, headers, query);
        if (!securitySatisfied)
        {
            return (null, $"認証要件を満たせない: {missingSecurity}（headers に認証情報を設定してください）");
        }

        JsonNode? body = null;
        if (operation.RequestBody is { } requestBody)
        {
            if (overrideEntry is { HasBody: true })
            {
                body = overrideEntry.Body;
            }
            else if (requestBody.Content is { Count: > 0 } content)
            {
                var media = content.TryGetValue("application/json", out var json)
                    ? json
                    : content.Values.First();
                body = media.Example
                    ?? media.Examples?.Values.FirstOrDefault()?.Value
                    ?? media.Schema?.Example;
            }

            if (body is null && requestBody.Required)
            {
                return (null, "requestBody に example／override が無い");
            }
        }

        var expectedStatus = overrideEntry?.ExpectedStatus ?? FindFirstSuccessStatus(operation.Responses);
        if (expectedStatus is null)
        {
            return (null, "2xx のレスポンス定義が仕様に無い");
        }

        return (new RequestPlan(method, pathKey, pathParams, query, headers, body, expectedStatus.Value), null);
    }

    private static string ParamKey(IOpenApiParameter p) => $"{p.In}:{p.Name}";

    private static string? ResolveParamValue(IOpenApiParameter param, OverrideEntry? overrideEntry)
    {
        var overrideMap = param.In switch
        {
            ParameterLocation.Path => overrideEntry?.PathParams,
            ParameterLocation.Query => overrideEntry?.Query,
            ParameterLocation.Header => overrideEntry?.Headers,
            _ => null,
        };
        if (!string.IsNullOrEmpty(param.Name) && overrideMap is not null
            && overrideMap.TryGetValue(param.Name, out var overridden))
        {
            return overridden;
        }

        var example = param.Example
            ?? param.Examples?.Values.FirstOrDefault()?.Value
            ?? param.Schema?.Example;
        return YamlJson.AsParamString(example);
    }

    /// <summary>
    /// レスポンス定義から最小の 2xx ステータスコードを選ぶ（決定的にするため数値昇順）。
    /// <c>"default"</c>・<c>"2XX"</c> 等のワイルドカードキーは対象外（v1 の対象外）。
    /// </summary>
    private static int? FindFirstSuccessStatus(OpenApiResponses? responses)
    {
        if (responses is null)
        {
            return null;
        }
        return responses.Keys
            .Select(k => int.TryParse(k, out var code) ? code : (int?)null)
            .Where(code => code is >= 200 and < 300)
            .OrderBy(code => code)
            .FirstOrDefault();
    }
}
