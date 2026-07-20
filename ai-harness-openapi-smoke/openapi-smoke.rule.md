---
paths:
  - .claude/harness/config/ai-harness-openapi-smoke.yml
---

## 概要

ai-harness-openapi-smoke は hook では発火せず、`ai-harness-main --fire ai-harness-openapi-smoke` から
手動起動する能動スキャン専用プラグイン。OpenAPI 仕様の各 operation（`paths` × method）へ実際に HTTP
リクエストを送り、正常系（happy path）の疎通を確認する。値の優先順位は `overrides` > 仕様の
`example`/`examples` > スキーマの `example`。解決できない必須項目がある operation は値を合成せず、
実リクエストを送らず**失敗（NG）**として報告する。判定はステータスコード（`expected_status` か仕様の
最小 2xx）とレスポンスボディの構造（`type`/`required`/`properties`/`items` の再帰照合のみ）。

- `spec` … OpenAPI 仕様（YAML/JSON・プロジェクトルート相対）
- `base_url` … テスト対象バックエンドのベース URL
- `headers` … 全リクエスト共通ヘッダ（省略可。適用順序は下記「ヘッダの適用順序」参照）
- `timeout_seconds` … リクエストタイムアウト秒（省略可・既定 10）
- `startup.cmd` / `startup.wait` / `startup.cwd` … `base_url` 無応答時だけ使うフォールバック起動
  （先頭から順に試し、`wait` 秒生存すれば採用）。既に応答していれば実行も停止もしない
- `sql` … `overrides` の `init`/`catch`/`final` で sql を使うなら必須の接続設定
  （`driver`: postgres/mysql、`host`/`port`/`database`/`username`/`password`。
  `username`/`password` は `${VAR_NAME}` で環境変数参照可）
- `overrides[].method` / `path` … 仕様の `paths` キーと method+path で紐付け
- `overrides[].path_params` / `query` / `headers` / `body` / `expected_status` … 仕様の example を上書き
- `overrides[].init` / `catch` / `final` … `cmd`（順次実行）と `sql`（クエリ結果のアサーション）。
  `init` はリクエスト前（失敗なら実リクエストを送らず NG）、`catch` は失敗時のみ、`final` は常に実行
- `auth.<schemeId>.login` … 仕様の `components.securitySchemes` のキー名ごとに、ログインリクエスト
  （`method`/`path`/`headers`/`body`/`token_field`）を書く。得たトークンの適用先（`Authorization`
  ヘッダか apiKey のヘッダ/クエリか）は仕様のスキーム定義から自動で導出する

### ヘッダの適用順序

送信する全リクエストのヘッダは、後勝ち（後述のものが先のものを上書き）で以下の順に重なる。

1. `auth.<schemeId>.login` で得たトークンの自動適用（`Authorization` ヘッダ、または apiKey スキームの
   ヘッダ/クエリ）。仕様の `components.securitySchemes` にスキームが無い、またはログイン自体が失敗した
   場合はこの層は乗らない
2. トップレベル `headers`（全リクエスト共通）
3. `overrides[].headers`（該当 operation のみ・仕様の example を上書き）

`auth.<schemeId>.login` 自体が送るログインリクエストにも、通常の operation と同じくトップレベル
`headers` が基底として適用される。`auth.<schemeId>.login.headers` を設定した場合はそちらが優先
（ログインリクエスト限定の 2. → `login.headers` という上書き）。全 operation がヘッダ経由の認証
（apiKey 等）を必須とする仕様では、トップレベル `headers` にその値を置かないとログインリクエスト自体が
拒否され `auth.<schemeId>.login` が常に失敗する。

仕様の `security`（認証要件）も、パラメータ・リクエストボディと同様に「解決できなければ NG」の対象。
上記の適用順序を経て最終的に解決済みの `headers`/`query` に、該当スキームの認証情報（`Authorization`
ヘッダ、または apiKey のヘッダ/クエリ）が無い operation は、実リクエストを送らず「認証要件を満たせない」
として NG になる。

設定不正・仕様が読めない場合は exit 2（検出。hook のゲートではないためブロックではなくレポート）。

`startup`（バックエンド自動起動）・`sql`（DB アサーション）・`auth`（ログイン）は Docker・DB・ログイン
エンドポイント等の環境が手元に無いことを理由に省略・コメントアウトしてよい機能ではない。これらは
「未実装」ではなく、この harness が正常系を実際に確認するための正式な機能。使うと判断したら、Docker
の起動・DB の用意・ログイン情報の設定まで含めて実際に動く状態に仕上げること。環境構築自体が難しい／
ユーザー判断が要るときは、省略して黙ってコメントアウトするのではなく、その旨をユーザーに確認する。

## 設定ファイル

`.claude/harness/config/ai-harness-openapi-smoke.yml`

```yaml
spec: docs/openapi.yaml
base_url: http://localhost:3000

startup:
  cmd:
    - ./venv/bin/python3 -m uvicorn main:app --host 0.0.0.0
    - ./venv/Scripts/python -m uvicorn main:app --host 0.0.0.0
  wait: 10

sql:
  driver: postgres
  host: localhost
  database: myapp_test
  username: postgres
  password: "${DB_PASSWORD}"

auth:
  bearerAuth:                    # 仕様の components.securitySchemes のキー名
    login:
      path: /auth/login
      body: { username: tester, password: "${TEST_PASSWORD}" }
      token_field: data.access_token

overrides:
  - method: GET
    path: /users/{id}
    path_params:
      id: "1"
    expected_status: 200
    init:
      sql:
        query: "SELECT COUNT(*) FROM users WHERE id = 1"
        result: "1"
    final:
      cmd:
        - some cleanup command
```
