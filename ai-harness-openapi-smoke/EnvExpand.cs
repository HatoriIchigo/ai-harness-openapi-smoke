using System.Text.RegularExpressions;

namespace ai_harness_openapi_smoke;

/// <summary>
/// 設定値中の <c>${VAR_NAME}</c> を環境変数へ展開する（sql 接続設定の username/password 向け）。
/// パターンを含まない値はそのままリテラルとして扱う。
/// </summary>
public static partial class EnvExpand
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Pattern();

    /// <summary>
    /// <paramref name="value"/> 中の <c>${VAR_NAME}</c> を展開する。未設定の環境変数があれば
    /// <paramref name="errors"/> に追記し、その箇所は空文字へ置換する（呼び出し側は設定不可として扱う）。
    /// </summary>
    public static string Expand(string value, List<string> errors) =>
        Pattern().Replace(value, m =>
        {
            var name = m.Groups[1].Value;
            var resolved = Environment.GetEnvironmentVariable(name);
            if (resolved is null)
            {
                errors.Add($"環境変数 '{name}' が未設定です。");
                return "";
            }
            return resolved;
        });
}
