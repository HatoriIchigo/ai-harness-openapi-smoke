using Microsoft.OpenApi;

namespace ai_harness_openapi_smoke;

/// <summary>
/// OpenAPI 仕様の <c>security</c>（認証要件）を、既に解決済みのリクエスト（<paramref name="headers"/>／
/// <paramref name="query"/>）が満たしているかを判定する。
///
/// <c>security</c> は「代替案のリスト」（配列の各要素のどれか 1 つを満たせばよい＝OR）で、各代替案自体は
/// 「スキーム名 → スコープ」のマップ（1 つの代替案内は全スキームを満たす必要がある＝AND）。空の代替案
/// （<c>{}</c>）は「認証不要」を意味し、それが選択肢に含まれていれば無条件で満たされる。
///
/// 認証情報そのもの（トークンの値等）は用意しない（既存の <c>headers</c>／<c>overrides[].headers</c> 等で
/// 用意する運用のまま）。ここでの役割は「必要な認証情報が既に用意されているか」の検出のみ。
/// 未充足を検出せずにリクエストを送ると、401/403 が「スキーマ違反」等の無関係な失敗として誤って報告される
/// ため、値の合成をしない他の必須項目（パラメータ・リクエストボディ）と同様に、ここで build 失敗として扱う。
/// </summary>
public static class SecurityResolver
{
    /// <summary>
    /// <paramref name="requirements"/>（<c>operation.Security ?? document.Security</c>）を満たせるか判定する。
    /// 満たせない場合、<c>MissingDescription</c> に代替案ごとの不足スキームを列挙する。
    /// </summary>
    public static (bool Satisfied, string? MissingDescription) Check(
        IReadOnlyList<OpenApiSecurityRequirement> requirements,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> query)
    {
        if (requirements.Count == 0)
        {
            return (true, null); // security 未定義、または空配列で明示的に認証不要。
        }

        var attempts = new List<string>();
        foreach (var requirement in requirements)
        {
            if (requirement.Count == 0)
            {
                return (true, null); // 空の代替案（認証不要）が選択肢にある。
            }

            var missing = requirement.Keys
                .Where(scheme => !IsSatisfied(scheme, headers, query))
                .Select(Describe)
                .ToList();

            if (missing.Count == 0)
            {
                return (true, null); // この代替案は全スキームを満たしている。
            }
            attempts.Add(string.Join(" + ", missing));
        }

        return (false, string.Join(" もしくは ", attempts));
    }

    private static bool IsSatisfied(
        OpenApiSecuritySchemeReference scheme,
        IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string> query)
    {
        return scheme.Type switch
        {
            // http（bearer/basic）・OAuth2・OpenID Connect はいずれも実務上 Authorization ヘッダで運ぶ。
            SecuritySchemeType.Http or SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect =>
                headers.ContainsKey("Authorization"),
            SecuritySchemeType.ApiKey when !string.IsNullOrEmpty(scheme.Name) => scheme.In switch
            {
                ParameterLocation.Header => headers.ContainsKey(scheme.Name),
                ParameterLocation.Query => query.ContainsKey(scheme.Name),
                // Cookie は個別のキー名までは見ず、Cookie ヘッダの有無だけを見る簡易判定（v1 のスコープ）。
                ParameterLocation.Cookie => headers.ContainsKey("Cookie"),
                _ => false,
            },
            // MutualTLS 等、ヘッダ／クエリで表現できない方式は解決不可（設定で満たしようが無い）。
            _ => false,
        };
    }

    private static string Describe(OpenApiSecuritySchemeReference scheme)
    {
        var id = scheme.Reference?.Id ?? "(unknown)";
        return scheme.Type switch
        {
            SecuritySchemeType.Http => $"{id}（Authorization ヘッダ）",
            SecuritySchemeType.OAuth2 => $"{id}（Authorization ヘッダ／OAuth2）",
            SecuritySchemeType.OpenIdConnect => $"{id}（Authorization ヘッダ／OpenID Connect）",
            SecuritySchemeType.ApiKey => $"{id}（{scheme.In} '{scheme.Name}'）",
            _ => $"{id}（この harness では解決できない認証方式: {scheme.Type}）",
        };
    }
}
