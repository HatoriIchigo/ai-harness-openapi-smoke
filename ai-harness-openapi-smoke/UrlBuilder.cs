using System.Text.RegularExpressions;

namespace ai_harness_openapi_smoke;

/// <summary>
/// <c>base_url</c> に対する絶対 URL の組み立て。operation のリクエスト（<see cref="OpenApiSmokePlugin"/>）と
/// 認証ログインリクエスト（<see cref="AuthResolver"/>）の両方から使う共通ロジック。
/// </summary>
public static class UrlBuilder
{
    /// <summary>
    /// <paramref name="pathTemplate"/> の <c>{param}</c> を <paramref name="pathParams"/> で埋め、
    /// <paramref name="baseUrl"/> に対する絶対 URL を組み立てる（query 付き）。
    /// </summary>
    public static (Uri? Uri, string? Error) Build(
        string baseUrl, string pathTemplate,
        IReadOnlyDictionary<string, string> pathParams, IReadOnlyDictionary<string, string> query)
    {
        var resolvedPath = Regex.Replace(pathTemplate, @"\{([^{}]+)\}", m =>
            pathParams.TryGetValue(m.Groups[1].Value, out var v) ? Uri.EscapeDataString(v) : m.Value);
        if (resolvedPath.Contains('{'))
        {
            return (null, $"パステンプレートに未解決のプレースホルダが残る: {resolvedPath}");
        }

        Uri combined;
        try
        {
            var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
            combined = new Uri(baseUri, resolvedPath.TrimStart('/'));
        }
        catch (Exception e)
        {
            return (null, $"URL を組み立てられない: {e.Message}");
        }

        if (query.Count == 0)
        {
            return (combined, null);
        }
        var qs = string.Join(
            "&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var builder = new UriBuilder(combined) { Query = qs };
        return (builder.Uri, null);
    }
}
