using System.Text;

namespace ai_harness_openapi_smoke;

/// <summary>
/// 設定に書かれたコマンドライン文字列をトークンへ分割する。空白区切り・二重引用符のみ対応の簡易実装
/// （シェルを経由せず直接 exec するため、パイプ・リダイレクト・変数展開等の複雑なシェル構文は非対応）。
/// </summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }
}
