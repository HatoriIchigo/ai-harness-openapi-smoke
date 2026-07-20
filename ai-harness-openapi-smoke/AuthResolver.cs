using System.Text;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ai_harness_openapi_smoke;

/// <summary>
/// 設定 <c>auth</c> に従ってログインリクエストを実行し、得られたトークンを仕様の
/// <c>components.securitySchemes</c> の定義（型・場所）に沿って自動的にヘッダ／クエリへ適用する。
/// どこへ適用するか（<c>Authorization</c> ヘッダか、apiKey のヘッダ／クエリか）は <c>auth</c> 側では指定させず、
/// 仕様のスキーム定義から導出する（ユーザーが二重に書く必要が無いようにするため）。
///
/// ログインに失敗したスキームは注入せず、警告ログを残すのみ（Fire 全体は止めない）。そのスキームを要求する
/// operation は、後段の <see cref="SecurityResolver"/> の判定で個別に失敗（NG）として報告される。
/// </summary>
public readonly record struct AuthResolveResult(
    IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, string> Query, IReadOnlyList<string> Logs);

public static class AuthResolver
{
    public static AuthResolveResult ResolveAll(
        HttpClient http, string baseUrl, OpenApiComponents? components,
        IReadOnlyDictionary<string, AuthLoginConfig> authConfigs,
        IReadOnlyDictionary<string, string> topLevelHeaders)
    {
        var headers = new Dictionary<string, string>();
        var query = new Dictionary<string, string>();
        var logs = new List<string>();

        foreach (var (schemeId, loginConfig) in authConfigs)
        {
            if (components?.SecuritySchemes?.TryGetValue(schemeId, out var scheme) != true)
            {
                logs.Add($"auth.{schemeId}: 仕様の components.securitySchemes に無いためログインをスキップ");
                continue;
            }

            var (token, error) = Login(http, baseUrl, loginConfig, topLevelHeaders);
            if (error is not null)
            {
                logs.Add(
                    $"auth.{schemeId}: ログインに失敗（この認証を要求する operation は個別に NG になる）: {error}");
                continue;
            }

            switch (scheme!.Type)
            {
                case SecuritySchemeType.Http or SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect:
                    headers["Authorization"] = $"Bearer {token}";
                    logs.Add($"auth.{schemeId}: ログイン成功（Authorization ヘッダへ適用）");
                    break;
                case SecuritySchemeType.ApiKey when !string.IsNullOrEmpty(scheme.Name):
                    if (scheme.In == ParameterLocation.Query)
                    {
                        query[scheme.Name] = token!;
                    }
                    else
                    {
                        // Cookie も含め、ヘッダ以外の細かい配送方式は v1 では簡易的にヘッダへ適用する。
                        headers[scheme.Name] = token!;
                    }
                    logs.Add($"auth.{schemeId}: ログイン成功（{scheme.In} '{scheme.Name}' へ適用）");
                    break;
                default:
                    logs.Add($"auth.{schemeId}: この harness では自動適用できない認証方式（{scheme.Type}）");
                    break;
            }
        }

        return new AuthResolveResult(headers, query, logs);
    }

    private static (string? Token, string? Error) Login(
        HttpClient http, string baseUrl, AuthLoginConfig config, IReadOnlyDictionary<string, string> topLevelHeaders)
    {
        var (uri, uriError) = UrlBuilder.Build(
            baseUrl, config.Path, new Dictionary<string, string>(), new Dictionary<string, string>());
        if (uriError is not null)
        {
            return (null, uriError);
        }

        // トップレベル headers が既定（ベース）で、login 固有の headers があればそちらを優先する
        // （RequestPlanner が override 側で defaultHeaders を上書きするのと同じ優先順位）。
        var mergedHeaders = new Dictionary<string, string>(topLevelHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in config.Headers)
        {
            mergedHeaders[name] = value;
        }

        using var request = new HttpRequestMessage(new HttpMethod(config.Method), uri);
        foreach (var (name, value) in mergedHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (config.Body is not null)
        {
            request.Content = new StringContent(config.Body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = http.Send(request);
        }
        catch (Exception e)
        {
            return (null, $"ログインリクエストの送信に失敗: {e.GetType().Name}: {e.Message}");
        }

        using (response)
        {
            string bodyText;
            using (var stream = response.Content.ReadAsStream())
            using (var reader = new StreamReader(stream))
            {
                bodyText = reader.ReadToEnd();
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"ログインが失敗ステータスを返した（{(int)response.StatusCode}）: {config.Method} {config.Path}");
            }

            JsonNode? bodyNode;
            try
            {
                bodyNode = JsonNode.Parse(bodyText);
            }
            catch (Exception e)
            {
                return (null, $"ログインのレスポンスが JSON として解析できない: {e.Message}");
            }

            var tokenNode = GetByPath(bodyNode, config.TokenField);
            var token = YamlJson.AsParamString(tokenNode);
            if (string.IsNullOrEmpty(token))
            {
                return (null, $"token_field '{config.TokenField}' をレスポンスから取り出せない");
            }
            return (token, null);
        }
    }

    /// <summary>ドット区切りのパス（例: <c>data.access_token</c>）で JSON をたどる。</summary>
    private static JsonNode? GetByPath(JsonNode? root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }
        return current;
    }
}
