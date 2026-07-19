# ai-harness-openapi-smoke

> OpenAPI 仕様を元にバックエンドへ実リクエストを送り、正常系（happy path）の疎通を確認する ai-harness プラグイン。

hook イベントには一切紐付かない。`ai-harness-main --fire ai-harness-openapi-smoke` から**手動で**起動する能動スキャン専用のプラグイン。仕様の各 operation（`paths` × method）へ実際に HTTP リクエストを送り、ステータスコードとレスポンスボディの構造を検証する。

## リクエストの組み立て

各 operation のパラメータ・リクエストボディは、次の優先順位で解決する。

1. 設定の `overrides`（method + path で仕様の operation に紐付ける）
2. 仕様の `example` / `examples`
3. スキーマの `example`

いずれからも値を解決できない**必須**パラメータ・必須リクエストボディがある operation、または 2xx のレスポンス定義が無い operation は、値を合成せずに**スキップ**する（失敗としては扱わない）。

## 判定

- **ステータスコード**: `overrides` の `expected_status`、無ければ仕様の 2xx レスポンスのうち最小のもの。
- **レスポンスボディ**: 実際のステータスに対応する `application/json` のスキーマがあれば、構造を照合する（`SchemaValidator`）。
  - 検証するのは `type` / `required` / `properties` / `items` の再帰照合のみ。`pattern` / `format` / `minimum` 等のフル JSON Schema 検証は対象外（正常系の疎通確認という v1 のスコープ）。
  - `IOpenApiSchema` を直接歩いて検証するため、OpenAPI 3.0 / 3.1 のスキーマ方言差（`nullable` vs 型配列 等）を JSON Schema へ変換する過程で吸収し損ねるリスクが無い。

いずれかの operation でステータス不一致・スキーマ違反があれば `exit 2`（検出）。`--fire` はスキャン結果のレポートであり、書き込みの差し戻しではない。

## 設定（config/ai-harness-openapi-smoke.yml）

```yaml
spec: docs/openapi.yaml            # OpenAPI 仕様（YAML / JSON・プロジェクトルート相対）
base_url: http://localhost:3000    # テスト対象バックエンドのベース URL

headers:                           # 全リクエスト共通ヘッダ（省略可）
  Authorization: "Bearer dev-token"

timeout_seconds: 10                # リクエストタイムアウト秒（省略可・既定 10）

# base_url が無応答のときだけ使うフォールバック起動（省略可）。
startup:
  cmd:
    - ./venv/bin/python3 -m uvicorn main:app --host 0.0.0.0     # 先頭から順に試す
    - ./venv/Scripts/python -m uvicorn main:app --host 0.0.0.0  # 上が起動できなければこちら（Windows venv 等）
  wait: 10                         # 起動コマンド実行後、この秒数生存すれば採用（未設定なら次を試す）
  cwd: backend                     # 起動コマンドの作業ディレクトリ（省略可・既定はプロジェクトルート）

# overrides の init/catch/final で sql を使うなら必須（省略可＝sql 未使用時は不要）。
sql:
  driver: postgres                  # postgres | mysql
  host: localhost
  port: 5432                        # 省略可（driver の既定ポート）
  database: myapp_test
  username: postgres
  password: "${DB_PASSWORD}"        # ${VAR_NAME} で環境変数参照。リテラルでも可

overrides:                         # example が無い／上書きしたい operation の指定（省略可）
  - method: GET
    path: /users/{id}              # 仕様の paths キーそのまま
    path_params:
      id: "1"
    query: {}                      # 省略可
    headers: {}                    # 省略可
    expected_status: 200           # 省略可（無ければ仕様の 2xx から自動選択）
    init:                          # リクエスト送信前（省略可）
      cmd:
        - some setup command
      sql:
        query: "SELECT COUNT(*) FROM users WHERE id = 1"
        result: "1"
    catch:                         # この operation が失敗したときだけ実行（省略可）
      cmd:
        - some cleanup-on-failure command
    final:                         # 成功でも失敗でも必ず実行（省略可）
      cmd:
        - some cleanup command

  - method: POST
    path: /users
    body:
      name: smoke-test
      email: smoke-test@example.com
    expected_status: 201
```

- `spec` / `base_url` は必須。`base_url` は絶対 URL。
- `overrides` の `path_params` / `query` / `headers` は文字列マップ。`body` は任意の YAML 値（JSON へ変換して送信、指定時は仕様の example より優先）。

### バックエンドの自動起動・停止（startup）

`base_url` へ TCP 接続確認（軽量な疎通チェック）を行い、**既に応答していれば `startup` は一切実行しない**（この harness が起動したプロセスではないため、終了時にも止めない＝他人が動かしているサーバーを勝手に落とさない）。

応答が無く `startup` も未設定なら、その時点でテストを実行できずエラーになる。`startup.cmd` があれば先頭から順に 1 つずつ起動を試し、**`wait` 秒の間に終了しなければ採用**する（環境依存で起動コマンドが変わる状況、例えば venv のレイアウト差（`venv/bin/python3` と `venv/Scripts/python`）を吸収する用途）。採用したプロセスは、この harness が起動した分だけを Fire 完了時（成功・失敗を問わず）に停止する。

`startup.cmd` はシェルを経由せず直接 exec するため、パイプ・リダイレクト・環境変数展開等の複雑なシェル構文は書けない（空白区切り・二重引用符のみの簡易パース）。

### init / catch / final

override には `init`（リクエスト送信前）・`catch`（この operation が失敗したときのみ）・`final`（成功でも失敗でも必ず）の 3 つのフックを書ける。いずれも `cmd`（順に全て実行するセットアップ／後始末スクリプト。`startup.cmd` の「フォールバック候補列」とは意味が異なる）と `sql`（クエリを実行し、結果をトップレベルの `result` と比較するアサーション）を持てる。

- `init` が失敗（`cmd` が非 0 終了、または `sql` の結果不一致・実行エラー）すると、その operation は**実リクエストを送らず NG として報告**する。
- `catch` は operation が失敗した（`init` 失敗、または送信・判定の失敗）ときのみ実行する。
- `final` は成功・失敗を問わず必ず実行する。
- `catch` / `final` 自身の失敗は、既に確定した operation の成否を変えない（警告ログとして表面化するのみ）。

`sql` チェックを使う override が 1 つでもあれば、トップレベルの `sql`（接続設定）が必須になる（無ければ設定エラーでフェイルクローズ）。`username` / `password` はリテラルでも `${VAR_NAME}` 形式の環境変数参照でもよい（環境変数が未設定なら設定エラー）。

## 対象外（v1 のスコープ外）

- 異常系（4xx/5xx を期待するテスト）は対象外。正常系のみ。
- リクエストボディ・パラメータの値を仕様のスキーマから合成する機能は無い（example／override が無ければスキップ）。
- `"2XX"` ワイルドカードや `"default"` のレスポンスキーは対象外（数値の 2xx キーのみ）。
- フル JSON Schema 検証（`pattern` / `format` / 数値範囲 等）は対象外。
- `sql` は PostgreSQL（Npgsql）・MySQL（MySqlConnector）のみ対応。
- `startup.cmd` / override の `cmd` はシェルを経由しない直接 exec（パイプ・リダイレクト等の複雑なシェル構文は不可）。

## エンジン

- [Microsoft.OpenApi](https://www.nuget.org/packages/Microsoft.OpenApi) / [Microsoft.OpenApi.YamlReader](https://www.nuget.org/packages/Microsoft.OpenApi.YamlReader) … OpenAPI 仕様のパース。仕様違反（`Diagnostic.Errors`）を検出したら仕様を信頼せずエラー扱いにする。
- [Npgsql](https://www.nuget.org/packages/Npgsql) / [MySqlConnector](https://www.nuget.org/packages/MySqlConnector) … `sql` チェックの DB 接続。
- `System.Net.Http.HttpClient`（同期 `Send`）… hook 系プラグインと同じ同期 `IEnumerable<LogEntry>` の契約に合わせ、非同期化はしない。

同一の仕様ファイルのパース結果はキャッシュする（更新時刻とサイズが変われば読み直す）。

## ビルドと配置

```sh
dotnet build ai-harness-openapi-smoke/ai-harness-openapi-smoke/ai-harness-openapi-smoke.csproj -c Release

# 配布物は lib の管理 DLL のみ（*.deps.json は不要。host の ALC が lib 直下を直接プローブする）。
# baselib を除く build 出力の *.dll を丸ごとコピーするのが確実（Npgsql 等の推移的依存を含むため）。
BIN=ai-harness-openapi-smoke/ai-harness-openapi-smoke/bin/Release/net10.0
for f in "$BIN"/*.dll; do
  [ "$(basename "$f")" = "ai-harness-baselib.dll" ] && continue
  cp "$f" <配置先>/lib/
done
# 現時点の内訳: ai-harness-openapi-smoke.dll, Microsoft.OpenApi.dll, Microsoft.OpenApi.YamlReader.dll,
# SharpYaml.dll, Npgsql.dll, MySqlConnector.dll, Microsoft.Extensions.Logging.Abstractions.dll,
# Microsoft.Extensions.DependencyInjection.Abstractions.dll（Npgsql の推移的依存）。

cp ai-harness-openapi-smoke/config/ai-harness-openapi-smoke.yml  <プロジェクト>/.claude/harness/config/

# common.yml の tools で有効化（--fire 専用のため daemon の再起動だけで反映される。hook の再起動は不要）:
<配置先>/ai-harness-main --plugin <プロジェクト> --enable ai-harness-openapi-smoke

# 実行:
cd <プロジェクト>
<配置先>/ai-harness-main --fire ai-harness-openapi-smoke
```

`baselib.dll` は host が共有ロードするため `lib/` に置かない（プラグイン出力にも含まれない）。

## 構成

```
ai-harness-openapi-smoke/
├── README.md
├── config/
│   └── ai-harness-openapi-smoke.yml   設定サンプル（配置元）
└── ai-harness-openapi-smoke/
    ├── ai-harness-openapi-smoke.csproj
    ├── OpenApiSmokePlugin.cs   Fire 本体・起動/停止・init/catch/final・HTTP 送信・判定・reason 生成
    ├── SmokeConfig.cs          設定の解釈とバリデーション
    ├── SpecLoader.cs           OpenAPI 仕様のパース（キャッシュ付き）
    ├── RequestPlanner.cs       operation ごとのリクエスト計画（override > example > スキーマ example）
    ├── SchemaValidator.cs      レスポンスボディの構造検証（type/required/properties/items）
    ├── BackendLauncher.cs      base_url の疎通確認・startup.cmd のフォールバック起動・停止
    ├── CmdRunner.cs            init/catch/final の cmd（順次実行）
    ├── SqlRunner.cs            init/catch/final の sql チェック（Npgsql / MySqlConnector）
    ├── CommandLine.cs          コマンドライン文字列の簡易トークン分割（共有）
    ├── EnvExpand.cs            ${VAR_NAME} の環境変数展開
    ├── YamlJson.cs             YAML 汎用値 ⇔ JsonNode の変換
    ├── openapi-smoke.rule.md   .claude/rules へ配布する rule（仕様追加時に override/example を促す）
    └── ai-harness-openapi-smoke.config.yml   有効化時に無ければ置くデフォルト設定
```
