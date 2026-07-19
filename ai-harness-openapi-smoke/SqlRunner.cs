using System.Data.Common;
using System.Globalization;
using MySqlConnector;
using Npgsql;

namespace ai_harness_openapi_smoke;

/// <summary>
/// override の init/catch/final の <c>sql</c> チェックを実行する。クエリを 1 件実行し、先頭行・先頭列の
/// スカラ値を文字列化して <see cref="SqlCheck.Result"/> と比較する（数値・文字列の型差は問わない緩い比較）。
/// PostgreSQL（Npgsql）・MySQL（MySqlConnector）に対応。
/// </summary>
public static class SqlRunner
{
    public static (bool Ok, string? Error) RunCheck(SqlConnectionConfig connectionConfig, SqlCheck check)
    {
        DbConnection connection;
        try
        {
            connection = CreateConnection(connectionConfig);
        }
        catch (Exception e)
        {
            return (false, $"sql 接続設定が不正: {e.Message}");
        }

        using (connection)
        {
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = check.Query;
                var result = command.ExecuteScalar();
                var actual = result is null or DBNull ? "" : Convert.ToString(result, CultureInfo.InvariantCulture) ?? "";
                if (!string.Equals(actual.Trim(), check.Result.Trim(), StringComparison.Ordinal))
                {
                    return (false, $"SQL 結果不一致（期待 '{check.Result}' / 実際 '{actual}'）: {check.Query}");
                }
                return (true, null);
            }
            catch (Exception e)
            {
                return (false, $"SQL 実行に失敗: {e.GetType().Name}: {e.Message} ({check.Query})");
            }
        }
    }

    private static DbConnection CreateConnection(SqlConnectionConfig c)
    {
        return c.Driver switch
        {
            "postgres" or "postgresql" or "pg" => new NpgsqlConnection(
                $"Host={c.Host};Port={c.Port};Database={c.Database};Username={c.Username};Password={c.Password}"),
            "mysql" => new MySqlConnection(
                $"Server={c.Host};Port={c.Port};Database={c.Database};Uid={c.Username};Pwd={c.Password}"),
            _ => throw new InvalidOperationException($"未対応の driver: {c.Driver}"),
        };
    }
}
