using System.Collections;
using System.Text.Json.Nodes;

namespace ai_harness_openapi_smoke;

/// <summary>SQL チェック 1 件。クエリを実行し、先頭行・先頭列のスカラ値を文字列として <see cref="Result"/> と比較する。</summary>
public sealed record SqlCheck(string Query, string Result);

/// <summary>
/// override の init/catch/final 1 件。<see cref="Cmd"/> は（<c>startup.cmd</c> と異なり）<b>順に全て実行</b>する
/// セットアップ／後始末スクリプトとして扱う。<see cref="Sql"/> は省略可（<c>Cmd</c> のみでもよい）。
/// </summary>
public sealed record HookSpec(IReadOnlyList<string> Cmd, SqlCheck? Sql)
{
    public bool IsEmpty => Cmd.Count == 0 && Sql is null;
}

/// <summary>
/// バックエンド自動起動の設定。<see cref="Cmd"/> は<b>フォールバック候補列</b>（先頭から順に試し、
/// <see cref="WaitSeconds"/> 秒生存すれば採用。生存しなければ次を試す）。venv レイアウト差（Unix/Windows）等、
/// 環境依存で起動コマンドが変わる状況を吸収する用途。
/// </summary>
public sealed record StartupConfig(IReadOnlyList<string> Cmd, int WaitSeconds, string? Cwd);

/// <summary>
/// override の sql チェックが使う DB 接続設定（プラグイン設定のトップレベルで 1 つだけ共有する）。
/// <see cref="Username"/>／<see cref="Password"/> は <c>${VAR_NAME}</c> 形式で環境変数を参照できる
/// （<see cref="EnvExpand"/> で展開済みの値がここに入る）。
/// </summary>
public sealed record SqlConnectionConfig(string Driver, string Host, int Port, string Database, string Username, string Password);

/// <summary>
/// override 1 件。<see cref="Method"/>／<see cref="Path"/>（OpenAPI 仕様のパステンプレートそのまま、例: <c>/users/{id}</c>）
/// で該当 operation に紐付ける。<see cref="Body"/> が非 null なら仕様の example より優先する。
///
/// <see cref="Init"/> はリクエスト送信前、<see cref="Catch"/> はこの operation が失敗したとき、
/// <see cref="Final"/> は成功・失敗を問わず最後に実行する（<c>Init</c> の失敗はこの operation 自体の失敗として扱い、
/// リクエストは送らない）。
/// </summary>
public sealed record OverrideEntry(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> PathParams,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    JsonNode? Body,
    bool HasBody,
    int? ExpectedStatus,
    HookSpec? Init,
    HookSpec? Catch,
    HookSpec? Final);

/// <summary>
/// ai-harness-openapi-smoke の設定を解釈・検証した結果。
///
/// 設定スキーマ:
/// <code>
/// spec: docs/openapi.yaml            # OpenAPI 仕様（YAML / JSON・プロジェクトルート相対）
/// base_url: http://localhost:3000    # テスト対象バックエンドのベース URL
/// headers:                           # 全リクエスト共通ヘッダ（省略可）
///   Authorization: "Bearer xxx"
/// timeout_seconds: 10                # リクエストタイムアウト秒（省略可・既定 10）
///
/// startup:                           # base_url 無応答時にのみ使うフォールバック起動（省略可）
///   cmd:
///     - ./venv/bin/python3 -m uvicorn main:app --host 0.0.0.0
///     - ./venv/Scripts/python -m uvicorn main:app --host 0.0.0.0
///   wait: 10
///   cwd: backend                     # 省略可（既定はプロジェクトルート）
///
/// sql:                                # init/catch/final で sql を使うなら必須（省略可＝sql 未使用時）
///   driver: postgres                  # postgres | mysql
///   host: localhost
///   port: 5432                        # 省略可（driver 既定値）
///   database: myapp_test
///   username: postgres
///   password: "${DB_PASSWORD}"        # ${VAR_NAME} で環境変数参照。リテラルでも可
///
/// overrides:                         # example が無い／上書きしたい operation の指定（省略可）
///   - method: GET
///     path: /users/{id}              # 仕様の paths キーと method+path で一致させる
///     path_params: { id: "1" }
///     query: { verbose: "true" }
///     headers: { X-Test: "1" }
///     expected_status: 200
///     init:
///       cmd: ["some setup command"]
///       sql: { query: "SELECT COUNT(*) FROM users WHERE id = 1", result: "1" }
///     catch:
///       cmd: ["some cleanup on failure"]
///     final:
///       cmd: ["some cleanup"]
/// </code>
/// </summary>
public sealed class SmokeConfig
{
    public string Spec { get; }
    public string BaseUrl { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public int TimeoutSeconds { get; }
    public StartupConfig? Startup { get; }
    public SqlConnectionConfig? SqlConnection { get; }
    public IReadOnlyList<OverrideEntry> Overrides { get; }
    public IReadOnlyList<string> Errors { get; }

    /// <summary>設定として使用可能か（必須項目が揃い、エラーが無い）。</summary>
    public bool IsUsable => Errors.Count == 0;

    private const int DefaultTimeoutSeconds = 10;
    private const int DefaultPostgresPort = 5432;
    private const int DefaultMySqlPort = 3306;

    private SmokeConfig(
        string spec, string baseUrl, IReadOnlyDictionary<string, string> headers, int timeoutSeconds,
        StartupConfig? startup, SqlConnectionConfig? sqlConnection,
        IReadOnlyList<OverrideEntry> overrides, IReadOnlyList<string> errors)
    {
        Spec = spec;
        BaseUrl = baseUrl;
        Headers = headers;
        TimeoutSeconds = timeoutSeconds;
        Startup = startup;
        SqlConnection = sqlConnection;
        Overrides = overrides;
        Errors = errors;
    }

    /// <summary>プラグインの <c>Config</c>（YAML マッピング）を解釈・検証する。</summary>
    public static SmokeConfig Parse(IReadOnlyDictionary<string, object> config)
    {
        var errors = new List<string>();

        var spec = GetRequiredScalar(config, "spec", errors);
        var baseUrl = GetRequiredScalar(config, "base_url", errors);
        if (baseUrl is not null && !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            errors.Add($"base_url が絶対 URL ではありません: '{baseUrl}'");
        }

        var headers = GetOptionalStringMap(config, "headers", "headers", errors);
        var timeoutSeconds = GetOptionalPositiveInt(config, "timeout_seconds", DefaultTimeoutSeconds, errors);
        var startup = ParseStartup(config, errors);
        var sqlConnection = ParseSqlConnection(config, errors);

        var overrides = new List<OverrideEntry>();
        if (config.TryGetValue("overrides", out var overridesRaw) && overridesRaw is not null)
        {
            if (overridesRaw is not IList overridesList || overridesRaw is string)
            {
                errors.Add("overrides はリストである必要があります。");
            }
            else
            {
                for (var i = 0; i < overridesList.Count; i++)
                {
                    ParseOverride(overridesList[i], i, overrides, errors);
                }
            }
        }

        var needsSql = overrides.Any(o =>
            o.Init?.Sql is not null || o.Catch?.Sql is not null || o.Final?.Sql is not null);
        if (needsSql && sqlConnection is null)
        {
            errors.Add("overrides の init/catch/final で sql を使っていますが、トップレベルの sql（接続設定）がありません。");
        }

        return new SmokeConfig(
            spec ?? "", baseUrl ?? "", headers, timeoutSeconds, startup, sqlConnection, overrides, errors);
    }

    private static StartupConfig? ParseStartup(IReadOnlyDictionary<string, object> config, List<string> errors)
    {
        if (!config.TryGetValue("startup", out var raw) || raw is null)
        {
            return null;
        }
        if (raw is not IDictionary map)
        {
            errors.Add("startup はマップである必要があります。");
            return null;
        }

        var cmd = ReadStringList(map, "cmd", "startup.cmd", errors);
        if (cmd.Count == 0)
        {
            errors.Add("startup.cmd には最低 1 件のコマンドが必要です。");
        }

        var wait = 0;
        if (map.Contains("wait"))
        {
            if (!int.TryParse(map["wait"]?.ToString(), out wait) || wait < 0)
            {
                errors.Add($"startup.wait は 0 以上の整数である必要があります: '{map["wait"]}'");
                wait = 0;
            }
        }

        var cwd = map.Contains("cwd") ? map["cwd"]?.ToString()?.Trim() : null;

        return new StartupConfig(cmd, wait, string.IsNullOrWhiteSpace(cwd) ? null : cwd);
    }

    private static SqlConnectionConfig? ParseSqlConnection(IReadOnlyDictionary<string, object> config, List<string> errors)
    {
        if (!config.TryGetValue("sql", out var raw) || raw is null)
        {
            return null;
        }
        if (raw is not IDictionary map)
        {
            errors.Add("sql はマップである必要があります。");
            return null;
        }

        var driver = GetScalar(map, "driver", "sql", errors)?.ToLowerInvariant();
        if (driver is not (null or "postgres" or "postgresql" or "pg" or "mysql"))
        {
            errors.Add($"sql.driver は postgres または mysql である必要があります: '{driver}'");
        }

        var host = GetScalar(map, "host", "sql", errors);
        var database = GetScalar(map, "database", "sql", errors);
        var username = GetScalar(map, "username", "sql", errors);
        // password は「認証不要」もあり得るため空文字を許容する（GetScalar は空をエラー扱いにするため使わない）。
        var password = map.Contains("password") ? map["password"]?.ToString() ?? "" : "";

        var defaultPort = driver == "mysql" ? DefaultMySqlPort : DefaultPostgresPort;
        var port = defaultPort;
        if (map.Contains("port") && !int.TryParse(map["port"]?.ToString(), out port))
        {
            errors.Add($"sql.port は整数である必要があります: '{map["port"]}'");
            port = defaultPort;
        }

        if (driver is null || host is null || database is null || username is null)
        {
            return null; // 個別のエラーは既に記録済み。
        }

        username = EnvExpand.Expand(username, errors);
        password = EnvExpand.Expand(password, errors);

        return new SqlConnectionConfig(driver, host, port, database, username, password);
    }

    private static void ParseOverride(object? item, int index, List<OverrideEntry> overrides, List<string> errors)
    {
        if (item is not IDictionary map)
        {
            errors.Add($"overrides[{index}]: method/path を持つマップである必要があります。");
            return;
        }

        var label = $"overrides[{index}]";
        var method = GetScalar(map, "method", label, errors)?.ToUpperInvariant();
        var path = GetScalar(map, "path", label, errors);
        if (method is null || path is null)
        {
            return;
        }
        if (!path.StartsWith('/'))
        {
            errors.Add($"{label}: path は '/' で始まる必要があります: '{path}'");
            return;
        }

        var pathParams = GetOptionalStringMap(map, "path_params", $"{label}.path_params", errors);
        var query = GetOptionalStringMap(map, "query", $"{label}.query", errors);
        var headers = GetOptionalStringMap(map, "headers", $"{label}.headers", errors);

        var hasBody = map.Contains("body");
        var body = hasBody ? YamlJson.ToJsonNode(map["body"]) : null;

        int? expectedStatus = null;
        if (map.Contains("expected_status"))
        {
            var raw = map["expected_status"]?.ToString();
            if (!int.TryParse(raw, out var parsed) || parsed is < 100 or > 599)
            {
                errors.Add($"{label}: expected_status は 100-599 の整数である必要があります: '{raw}'");
            }
            else
            {
                expectedStatus = parsed;
            }
        }

        var init = ParseHookSpec(map, "init", label, errors);
        var catchHook = ParseHookSpec(map, "catch", label, errors);
        var finalHook = ParseHookSpec(map, "final", label, errors);

        overrides.Add(new OverrideEntry(
            method, path, pathParams, query, headers, body, hasBody, expectedStatus, init, catchHook, finalHook));
    }

    private static HookSpec? ParseHookSpec(IDictionary overrideMap, string key, string label, List<string> errors)
    {
        if (!overrideMap.Contains(key) || overrideMap[key] is not { } raw)
        {
            return null;
        }
        if (raw is not IDictionary map)
        {
            errors.Add($"{label}.{key} はマップである必要があります。");
            return null;
        }

        var cmd = ReadStringList(map, "cmd", $"{label}.{key}.cmd", errors);

        SqlCheck? sql = null;
        if (map.Contains("sql") && map["sql"] is { } sqlRaw)
        {
            if (sqlRaw is not IDictionary sqlMap)
            {
                errors.Add($"{label}.{key}.sql はマップである必要があります。");
            }
            else
            {
                var sqlLabel = $"{label}.{key}.sql";
                var query = GetScalar(sqlMap, "query", sqlLabel, errors);
                var result = sqlMap.Contains("result") ? sqlMap["result"]?.ToString() : null;
                if (result is null)
                {
                    errors.Add($"{sqlLabel}: result が未設定です。");
                }
                if (query is not null && result is not null)
                {
                    sql = new SqlCheck(query, result);
                }
            }
        }

        return new HookSpec(cmd, sql);
    }

    /// <summary>非空の単一スカラを取り出す。未設定・リストはエラーとして記録し null を返す。</summary>
    private static string? GetRequiredScalar(IReadOnlyDictionary<string, object> config, string key, List<string> errors)
    {
        if (!config.TryGetValue(key, out var raw) || raw is null)
        {
            errors.Add($"{key} が未設定です。");
            return null;
        }
        if (raw is IList or IDictionary)
        {
            errors.Add($"{key} はスカラ値である必要があります。");
            return null;
        }
        var value = raw.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} が未設定です。");
            return null;
        }
        return value;
    }

    private static string? GetScalar(IDictionary map, string key, string label, List<string> errors)
    {
        var raw = map.Contains(key) ? map[key] : null;
        if (raw is IList or IDictionary)
        {
            errors.Add($"{label}: {key} はスカラ値である必要があります。");
            return null;
        }
        var value = raw?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label}: {key} が未設定です。");
            return null;
        }
        return value;
    }

    private static IReadOnlyList<string> ReadStringList(IDictionary map, string key, string label, List<string> errors)
    {
        if (!map.Contains(key) || map[key] is not { } raw)
        {
            return [];
        }
        if (raw is not IList list || raw is string)
        {
            errors.Add($"{label} はリストである必要があります。");
            return [];
        }
        var result = new List<string>();
        foreach (var item in list)
        {
            var s = item?.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                result.Add(s);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> GetOptionalStringMap(
        IReadOnlyDictionary<string, object> config, string key, string label, List<string> errors)
    {
        if (!config.TryGetValue(key, out var raw) || raw is null)
        {
            return new Dictionary<string, string>();
        }
        return ToStringMap(raw, key, label, errors);
    }

    private static IReadOnlyDictionary<string, string> GetOptionalStringMap(
        IDictionary map, string key, string label, List<string> errors)
    {
        if (!map.Contains(key) || map[key] is not { } raw)
        {
            return new Dictionary<string, string>();
        }
        return ToStringMap(raw, key, label, errors);
    }

    private static IReadOnlyDictionary<string, string> ToStringMap(
        object raw, string key, string label, List<string> errors)
    {
        if (raw is not IDictionary dict)
        {
            errors.Add($"{label}: {key} はマップである必要があります。");
            return new Dictionary<string, string>();
        }
        var result = new Dictionary<string, string>();
        foreach (DictionaryEntry entry in dict)
        {
            var k = entry.Key?.ToString();
            var v = entry.Value?.ToString();
            if (!string.IsNullOrEmpty(k) && v is not null)
            {
                result[k] = v;
            }
        }
        return result;
    }

    private static int GetOptionalPositiveInt(
        IReadOnlyDictionary<string, object> config, string key, int defaultValue, List<string> errors)
    {
        if (!config.TryGetValue(key, out var raw) || raw is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(raw.ToString(), out var value) || value <= 0)
        {
            errors.Add($"{key} は正の整数である必要があります: '{raw}'");
            return defaultValue;
        }
        return value;
    }
}
