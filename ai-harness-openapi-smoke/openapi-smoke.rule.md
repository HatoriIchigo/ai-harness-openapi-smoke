---
paths:
  - .claude/harness/config/ai-harness-openapi-smoke.yml
---

## 概要

ai-harness-openapi-smoke は hook では発火せず、`ai-harness-main --fire ai-harness-openapi-smoke` から
手動起動する能動スキャン専用プラグイン。OpenAPI 仕様の各 operation（`paths` × method）へ実際に HTTP
リクエストを送り、正常系（happy path）の疎通を確認する。値の優先順位は `overrides` > 仕様の
`example`/`examples` > スキーマの `example`。解決できない必須項目がある operation は値を合成せず
スキップする（失敗ではない）。判定はステータスコード（`expected_status` か仕様の最小 2xx）とレスポンス
ボディの構造（`type`/`required`/`properties`/`items` の再帰照合のみ）。

- `spec` … OpenAPI 仕様（YAML/JSON・プロジェクトルート相対）
- `base_url` … テスト対象バックエンドのベース URL
- `headers` … 全リクエスト共通ヘッダ（省略可）
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

設定不正・仕様が読めない場合は exit 2（検出。hook のゲートではないためブロックではなくレポート）。

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
