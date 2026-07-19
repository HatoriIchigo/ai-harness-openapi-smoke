using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ai_harness_baselib;
using Microsoft.OpenApi;

namespace ai_harness_openapi_smoke;

/// <summary>
/// OpenAPI 仕様を元にバックエンドへ実リクエストを投げ、正常系（happy path）の疎通を確認するプラグイン。
///
/// hook イベントには一切紐付かず（<see cref="Tools"/>／<see cref="Events"/>／<see cref="FileNames"/>／
/// <see cref="BashCommands"/> は全て既定の <c>null</c>）、<c>ai-harness-main --fire ai-harness-openapi-smoke</c>
/// から手動で起動する能動スキャン専用のプラグイン。
///
/// 各 operation について、値の優先順位は override（設定の <c>overrides</c>） &gt; 仕様の example &gt;
/// スキーマの example。必須パラメータ・必須リクエストボディ・2xx レスポンス定義のいずれかが解決できない
/// operation は「値を合成しない」方針のためスキップする（失敗ではない）。
///
/// 判定はステータスコード（2xx のうち最小のものを既定の期待値とする） &amp; レスポンスボディのスキーマ照合
/// （<see cref="SchemaValidator"/>。type／required／properties／items の構造チェックのみ）。
/// </summary>
public sealed class OpenApiSmokePlugin : PluginBase
{
    public override string PluginName => "ai-harness-openapi-smoke";

    public override string Description =>
        "OpenAPI 仕様を元にバックエンドへ実リクエストを送り、正常系の疎通を確認する";

    public override string ConfigName => "ai-harness-openapi-smoke.yml";

    /// <summary>埋め込み rule（<c>openapi-smoke.rule.md</c>）を各プロジェクトの <c>.claude/rules</c> へ配布する。</summary>
    public override bool ProvidesRule => true;

    public override IEnumerable<LogEntry> Init()
    {
        yield return LogEntry.Info("初期化");
    }

    /// <summary>hook 発火は無い（Tools/Events/FileNames/BashCommands が全て null）ため、常に no-op。</summary>
    public override IEnumerable<LogEntry> Action(HookData data, PluginResult result)
    {
        yield break;
    }

    public override IEnumerable<LogEntry> Fire(string projectRoot, PluginResult result)
    {
        var config = SmokeConfig.Parse(Config);
        if (!config.IsUsable)
        {
            yield return LogEntry.Warning("設定が使用不可のためテストを実行できない");
            result.ExitCode = 2;
            result.Reason = BuildConfigErrorReason(config.Errors);
            yield break;
        }

        var specPath = Path.GetFullPath(Path.Combine(projectRoot, config.Spec));
        var spec = SpecLoader.Load(specPath);
        if (spec.Document is null)
        {
            yield return LogEntry.Warning("OpenAPI 仕様を読めないためテストを実行できない");
            result.ExitCode = 2;
            result.Reason = BuildSpecErrorReason(spec.Errors);
            yield break;
        }

        var backend = BackendLauncher.EnsureRunning(config.Startup, config.BaseUrl, projectRoot);
        foreach (var log in backend.Logs)
        {
            yield return LogEntry.Info(log);
        }
        if (!backend.Ready)
        {
            yield return LogEntry.Warning("バックエンドを用意できないためテストを実行できない");
            result.ExitCode = 2;
            result.Reason = backend.Error ?? "バックエンドを用意できません。";
            yield break;
        }

        yield return LogEntry.Info($"OpenAPI smoke テスト開始 base_url={config.BaseUrl} spec={config.Spec}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };

        var passed = new List<string>();
        var skipped = new List<string>();
        var failed = new List<FailureDetail>();

        try
        {
            foreach (var pathEntry in spec.Document.Paths.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var pathKey = pathEntry.Key;
                var pathItem = pathEntry.Value;

                var operations = pathItem.Operations ?? new Dictionary<HttpMethod, OpenApiOperation>();
                foreach (var opEntry in operations.OrderBy(kv => kv.Key.Method, StringComparer.Ordinal))
                {
                    var method = opEntry.Key;
                    var operation = opEntry.Value;
                    var label = $"{method.Method} {pathKey}";

                    var overrideEntry = config.Overrides.FirstOrDefault(o =>
                        string.Equals(o.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(o.Path, pathKey, StringComparison.Ordinal));

                    var (plan, skipReason) = RequestPlanner.Build(
                        pathKey, method, pathItem.Parameters ?? [], operation, overrideEntry, config.Headers);

                    if (skipReason is not null)
                    {
                        yield return LogEntry.Debug($"スキップ: {label}（{skipReason}）");
                        skipped.Add(label);
                        continue;
                    }

                    var built = plan!;
                    var (uri, uriError) = BuildUri(config.BaseUrl, built.PathTemplate, built.PathParams, built.Query);
                    if (uriError is not null)
                    {
                        yield return LogEntry.Warning($"スキップ: {label}（{uriError}）");
                        skipped.Add(label);
                        continue;
                    }

                    bool operationFailed;
                    FailureDetail? failureDetail;

                    var initResult = RunHook(overrideEntry?.Init, config, projectRoot);
                    if (!initResult.Ok)
                    {
                        var msg = $"init 失敗: {initResult.Error}";
                        yield return LogEntry.Warning($"NG: {label}（{msg}）");
                        failureDetail = new FailureDetail(label, [msg]);
                        operationFailed = true;
                    }
                    else
                    {
                        var outcome = SendAndValidate(http, label, built, uri!, operation);
                        if (outcome.Failure is { } failure)
                        {
                            yield return LogEntry.Warning($"NG: {label}（{string.Join("; ", failure.Violations)}）");
                            failureDetail = failure;
                            operationFailed = true;
                        }
                        else
                        {
                            yield return LogEntry.Info($"ok: {label} -> {outcome.ActualStatus}");
                            failureDetail = null;
                            operationFailed = false;
                        }
                    }

                    foreach (var msg in RunCatchAndFinal(overrideEntry, config, projectRoot, operationFailed))
                    {
                        yield return LogEntry.Warning($"{label}: {msg}");
                    }

                    if (operationFailed)
                    {
                        failed.Add(failureDetail!);
                    }
                    else
                    {
                        passed.Add(label);
                    }
                }
            }
        }
        finally
        {
            BackendLauncher.Stop(backend.Owned);
        }

        yield return LogEntry.Info(
            $"完了: 成功 {passed.Count} 件 / 失敗 {failed.Count} 件 / スキップ {skipped.Count} 件");

        if (failed.Count == 0)
        {
            yield break; // ExitCode 0（許可）のまま
        }

        result.ExitCode = 2;
        result.Reason = BuildFailureReason(failed, skipped.Count, passed.Count);
    }

    /// <summary>
    /// override の init/catch/final 1 件を実行する（<c>cmd</c> を順に実行後、あれば <c>sql</c> チェック）。
    /// <paramref name="spec"/> が null／空なら何もせず成功扱い。
    /// </summary>
    private static (bool Ok, string? Error) RunHook(HookSpec? spec, SmokeConfig config, string projectRoot)
    {
        if (spec is null || spec.IsEmpty)
        {
            return (true, null);
        }
        if (spec.Cmd.Count > 0)
        {
            var (cmdOk, cmdError) = CmdRunner.RunSequential(spec.Cmd, projectRoot);
            if (!cmdOk)
            {
                return (false, cmdError);
            }
        }
        if (spec.Sql is { } sql)
        {
            // config.SqlConnection は SmokeConfig.Parse の相互検証（sql 使用時は必須）で non-null を保証済み。
            var (sqlOk, sqlError) = SqlRunner.RunCheck(config.SqlConnection!, sql);
            if (!sqlOk)
            {
                return (false, sqlError);
            }
        }
        return (true, null);
    }

    /// <summary>
    /// operation 終了後の後始末。<paramref name="operationFailed"/> のときのみ <c>catch</c> を実行し、
    /// <c>final</c> は常に実行する。catch/final 自体の失敗は既に確定した成否を変えず、警告として返すのみ。
    /// </summary>
    private static IReadOnlyList<string> RunCatchAndFinal(
        OverrideEntry? overrideEntry, SmokeConfig config, string projectRoot, bool operationFailed)
    {
        var messages = new List<string>();
        if (operationFailed && overrideEntry?.Catch is { IsEmpty: false } catchHook)
        {
            var (ok, error) = RunHook(catchHook, config, projectRoot);
            if (!ok)
            {
                messages.Add($"catch 失敗: {error}");
            }
        }
        if (overrideEntry?.Final is { IsEmpty: false } finalHook)
        {
            var (ok, error) = RunHook(finalHook, config, projectRoot);
            if (!ok)
            {
                messages.Add($"final 失敗: {error}");
            }
        }
        return messages;
    }

    /// <summary>1 operation の送信・検証結果。<see cref="Failure"/> が null なら成功。</summary>
    private sealed record SendOutcome(int? ActualStatus, FailureDetail? Failure);

    /// <summary>1 operation のリクエスト送信とレスポンス検証。iterator の外に出し、例外・using を素直に扱う。</summary>
    private static SendOutcome SendAndValidate(
        HttpClient http, string label, RequestPlan plan, Uri uri, OpenApiOperation operation)
    {
        using var request = new HttpRequestMessage(plan.Method, uri);
        foreach (var (headerName, headerValue) in plan.Headers)
        {
            if (!IsContentHeader(headerName))
            {
                request.Headers.TryAddWithoutValidation(headerName, headerValue);
            }
        }
        if (plan.Body is not null)
        {
            request.Content = new StringContent(plan.Body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = http.Send(request);
        }
        catch (Exception e)
        {
            var msg = $"リクエスト送信に失敗: {e.GetType().Name}: {e.Message}";
            return new SendOutcome(null, new FailureDetail(label, [msg]));
        }

        using (response)
        {
            var actualStatus = (int)response.StatusCode;
            var violations = new List<string>();
            if (actualStatus != plan.ExpectedStatus)
            {
                violations.Add($"ステータスコード不一致（期待 {plan.ExpectedStatus} / 実際 {actualStatus}）");
            }

            IOpenApiMediaType? media = null;
            if (operation.Responses?.TryGetValue(actualStatus.ToString(), out var responseSpec) == true)
            {
                responseSpec.Content?.TryGetValue("application/json", out media);
            }

            if (media?.Schema is { } schema)
            {
                string bodyText;
                using (var stream = response.Content.ReadAsStream())
                using (var reader = new StreamReader(stream))
                {
                    bodyText = reader.ReadToEnd();
                }

                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    JsonNode? bodyNode = null;
                    try
                    {
                        bodyNode = JsonNode.Parse(bodyText);
                    }
                    catch (Exception e)
                    {
                        violations.Add($"レスポンスボディが JSON として解析できない: {e.Message}");
                    }
                    if (bodyNode is not null)
                    {
                        violations.AddRange(SchemaValidator.Validate(schema, bodyNode));
                    }
                }
            }

            return violations.Count == 0
                ? new SendOutcome(actualStatus, null)
                : new SendOutcome(actualStatus, new FailureDetail(label, violations));
        }
    }

    private static bool IsContentHeader(string name) =>
        string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="pathTemplate"/> の <c>{param}</c> を <paramref name="pathParams"/> で埋め、
    /// <paramref name="baseUrl"/> に対する絶対 URL を組み立てる（query 付き）。
    /// </summary>
    private static (Uri? Uri, string? Error) BuildUri(
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

    /// <summary>失敗 1 件（operation ラベルと違反理由の一覧）。</summary>
    private sealed record FailureDetail(string Label, IReadOnlyList<string> Violations);

    private const int MaxReportedFailures = 50;

    private static string BuildFailureReason(IReadOnlyList<FailureDetail> failed, int skippedCount, int passedCount)
    {
        var sb = new StringBuilder();
        sb.Append("OpenAPI smoke テストで ").Append(failed.Count).Append(" 件失敗しました")
            .Append("（成功 ").Append(passedCount).Append(" 件 / スキップ ").Append(skippedCount).Append(" 件）:\n");
        foreach (var f in failed.Take(MaxReportedFailures))
        {
            sb.Append("- ").Append(f.Label).Append(": ").Append(string.Join("; ", f.Violations)).Append('\n');
        }
        if (failed.Count > MaxReportedFailures)
        {
            sb.Append("- …ほか ").Append(failed.Count - MaxReportedFailures).Append(" 件\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string BuildSpecErrorReason(IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("OpenAPI 仕様を読めないため smoke テストを実行できません:\n- ");
        sb.Append(string.Join("\n- ", errors));
        return sb.ToString();
    }

    private static string BuildConfigErrorReason(IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("ai-harness-openapi-smoke の設定が不正です。ai-harness-openapi-smoke.yml を修正してください:\n- ");
        sb.Append(string.Join("\n- ", errors));
        return sb.ToString();
    }
}
