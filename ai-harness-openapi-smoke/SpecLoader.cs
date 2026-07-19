using System.Collections.Concurrent;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace ai_harness_openapi_smoke;

/// <summary>OpenAPI 仕様のパース結果。<see cref="Errors"/> が非空なら <see cref="Document"/> は信頼できない。</summary>
public readonly record struct SpecLoadResult(OpenApiDocument? Document, IReadOnlyList<string> Errors);

/// <summary>
/// OpenAPI 仕様（YAML / JSON）を Microsoft.OpenApi でパースする。<c>ai-harness-api-paths</c> の
/// <c>SpecPaths</c> と同じキャッシュ方針（更新時刻・サイズが変わらなければ再パースしない）。
/// </summary>
public static class SpecLoader
{
    private readonly record struct CacheKey(string Path, long Ticks, long Length);

    private static readonly ConcurrentDictionary<CacheKey, SpecLoadResult> Cache = new();

    /// <summary>仕様ファイル（絶対パス）を読み、<see cref="OpenApiDocument"/> を返す。読めない・壊れている場合は <see cref="SpecLoadResult.Errors"/> に理由。</summary>
    public static SpecLoadResult Load(string specPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(specPath);
            if (!info.Exists)
            {
                return Failed($"OpenAPI 仕様が見つかりません: {specPath}");
            }
        }
        catch (Exception e)
        {
            return Failed($"OpenAPI 仕様を参照できません: {specPath} ({e.GetType().Name})");
        }

        var key = new CacheKey(specPath, info.LastWriteTimeUtc.Ticks, info.Length);
        return Cache.GetOrAdd(key, k => Parse(k.Path));
    }

    private static SpecLoadResult Parse(string specPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(specPath);
        }
        catch (Exception e)
        {
            return Failed($"OpenAPI 仕様を読めません: {specPath} ({e.GetType().Name})");
        }

        var format = Path.GetExtension(specPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : "yaml";

        ReadResult read;
        try
        {
            var settings = new OpenApiReaderSettings();
            settings.AddYamlReader();
            read = OpenApiDocument.Parse(text, format, settings);
        }
        catch (Exception e)
        {
            return Failed($"OpenAPI 仕様のパースに失敗: {specPath} ({e.GetType().Name}: {e.Message})");
        }

        var diagnosticErrors = read.Diagnostic?.Errors ?? [];
        if (diagnosticErrors.Count > 0)
        {
            var errors = diagnosticErrors
                .Take(MaxReportedSpecErrors)
                .Select(e => $"{specPath}: {e.Message}")
                .ToList();
            if (diagnosticErrors.Count > MaxReportedSpecErrors)
            {
                errors.Add($"{specPath}: …ほか {diagnosticErrors.Count - MaxReportedSpecErrors} 件");
            }
            return new SpecLoadResult(null, errors);
        }

        if (read.Document?.Paths is null)
        {
            return Failed($"OpenAPI 仕様に paths がありません: {specPath}");
        }

        return new SpecLoadResult(read.Document, []);
    }

    /// <summary>reason に列挙する仕様エラーの最大件数。</summary>
    private const int MaxReportedSpecErrors = 10;

    private static SpecLoadResult Failed(string error) => new(null, [error]);
}
