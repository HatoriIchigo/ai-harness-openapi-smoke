using System.Diagnostics;

namespace ai_harness_openapi_smoke;

/// <summary>override の init/catch/final の <c>cmd</c> を実行する。<c>startup.cmd</c>（フォールバック候補の列）とは
/// 意味が異なり、こちらは<b>順に全て実行する</b>セットアップ／後始末スクリプトとして扱う。</summary>
public static class CmdRunner
{
    /// <summary>1 コマンドに許す最大実行時間（秒）。init/catch/final はテスト対象のリクエストごとに走るため、
    /// ハング時に全体を止めないよう固定の上限を設ける（v1 では設定不可）。</summary>
    private const int TimeoutSeconds = 30;

    /// <summary><paramref name="commands"/> を順に実行する。途中で失敗したら以降は実行せず打ち切る。</summary>
    public static (bool Ok, string? Error) RunSequential(IReadOnlyList<string> commands, string cwd)
    {
        foreach (var commandLine in commands)
        {
            var (ok, error) = RunOne(commandLine, cwd);
            if (!ok)
            {
                return (false, error);
            }
        }
        return (true, null);
    }

    private static (bool Ok, string? Error) RunOne(string commandLine, string cwd)
    {
        var parts = CommandLine.Split(commandLine);
        if (parts.Count == 0)
        {
            return (true, null); // 空行は無視
        }

        Process? process;
        try
        {
            var psi = new ProcessStartInfo(parts[0])
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            for (var i = 1; i < parts.Count; i++)
            {
                psi.ArgumentList.Add(parts[i]);
            }
            process = Process.Start(psi);
        }
        catch (Exception e)
        {
            return (false, $"コマンドを起動できない: {commandLine} ({e.GetType().Name}: {e.Message})");
        }

        if (process is null)
        {
            return (false, $"コマンドを起動できない: {commandLine}");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutSeconds * 1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { /* 既に終了 */ }
                return (false, $"コマンドがタイムアウト（{TimeoutSeconds}秒）: {commandLine}");
            }

            if (process.ExitCode != 0)
            {
                var stderr = stderrTask.Result.Trim();
                var suffix = string.IsNullOrEmpty(stderr) ? "" : $": {stderr}";
                return (false, $"コマンドが終了コード {process.ExitCode} で失敗: {commandLine}{suffix}");
            }

            _ = stdoutTask.Result; // 読み切ってバッファ滞留を防ぐ（内容は使わない）
            return (true, null);
        }
    }
}
