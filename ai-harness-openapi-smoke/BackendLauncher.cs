using System.Diagnostics;
using System.Net.Sockets;

namespace ai_harness_openapi_smoke;

/// <summary>起動結果。<see cref="Owned"/> が非 null なら、この harness 自身が起動したプロセス（終了時に停止する責任を持つ）。</summary>
public readonly record struct BackendStartResult(bool Ready, Process? Owned, IReadOnlyList<string> Logs, string? Error);

/// <summary>
/// <c>base_url</c> のバックエンドを用意する。<c>base_url</c> が既に応答していれば何も起動せず（＝この harness が
/// 管理するプロセスではないため、終了時にも止めない）、応答が無ければ <c>startup.cmd</c> を先頭から順に試し、
/// 起動直後に落ちなければ（<c>startup.wait</c> 秒生存すれば）採用する。クロスプラットフォームの
/// venv レイアウト差（<c>venv/bin/python3</c> と <c>venv/Scripts/python</c>）等をフォールバック列で吸収する用途。
/// </summary>
public static class BackendLauncher
{
    /// <summary>既に listen 中かどうかを見る TCP 接続確認のタイムアウト。</summary>
    private const int ProbeTimeoutMs = 500;

    /// <summary>生存確認のポーリング間隔（ミリ秒）。</summary>
    private const int PollIntervalMs = 200;

    public static BackendStartResult EnsureRunning(StartupConfig? startup, string baseUrl, string projectRoot)
    {
        var logs = new List<string>();

        if (IsListening(baseUrl))
        {
            logs.Add($"base_url は既に応答しているため起動処理をスキップ: {baseUrl}");
            return new BackendStartResult(true, null, logs, null);
        }

        if (startup is null)
        {
            return new BackendStartResult(
                false, null, logs,
                $"base_url に応答が無く、startup が未設定のためテストを実行できない: {baseUrl}");
        }

        var cwd = string.IsNullOrWhiteSpace(startup.Cwd)
            ? projectRoot
            : Path.GetFullPath(Path.Combine(projectRoot, startup.Cwd));

        foreach (var commandLine in startup.Cmd)
        {
            logs.Add($"起動試行: {commandLine}（cwd={cwd}）");
            var process = TryLaunch(commandLine, cwd);
            if (process is null)
            {
                logs.Add($"起動失敗（プロセスを開始できない）: {commandLine}");
                continue;
            }

            var exitedEarly = WaitOrExit(process, startup.WaitSeconds);
            if (exitedEarly)
            {
                logs.Add($"起動直後に終了（exit={process.ExitCode}）: {commandLine}");
                process.Dispose();
                continue;
            }

            logs.Add($"起動成功（{startup.WaitSeconds}秒待機済み）: {commandLine}");
            return new BackendStartResult(true, process, logs, null);
        }

        return new BackendStartResult(false, null, logs, "startup.cmd のいずれも起動できなかった");
    }

    /// <summary><paramref name="owned"/> が非 null（この harness が起動したプロセス）なら停止する。</summary>
    public static void Stop(Process? owned)
    {
        if (owned is null)
        {
            return;
        }
        try
        {
            if (!owned.HasExited)
            {
                owned.Kill(entireProcessTree: true);
                owned.WaitForExit(5000);
            }
        }
        catch { /* 既に終了・停止不能はここでは無視 */ }
        finally
        {
            owned.Dispose();
        }
    }

    private static bool IsListening(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(uri.Host, port);
            return connectTask.Wait(ProbeTimeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static Process? TryLaunch(string commandLine, string cwd)
    {
        var parts = CommandLine.Split(commandLine);
        if (parts.Count == 0)
        {
            return null;
        }
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
            var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }
            // 起動したまま常駐するプロセスの出力を誰も読まないと、バッファ滞留でハングし得るため読み捨てる。
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            return null;
        }
    }

    /// <summary><paramref name="waitSeconds"/> 秒の間ポーリングし、その間に終了したら true（＝起動失敗とみなす）。</summary>
    private static bool WaitOrExit(Process process, int waitSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return true;
            }
            Thread.Sleep(PollIntervalMs);
        }
        return process.HasExited;
    }
}
