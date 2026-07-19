---
paths:
  - .claude/harness/config/ai-harness-openapi-smoke.yml
  - "**/openapi.yaml"
  - "**/openapi.yml"
  - "**/openapi.json"
---

## 概要

このプロジェクトには ai-harness-openapi-smoke（OpenAPI 仕様を元にバックエンドへ実リクエストを送り、
正常系（happy path）の疎通を確認する ai-harness プラグイン）が導入されている。hook では発火せず、
`ai-harness-main --fire ai-harness-openapi-smoke` で手動実行する。

## OpenAPI 仕様・エンドポイントを追加・変更するときの注意

各 operation（`paths` × method）のリクエストは、値の優先順位
**設定の `overrides`（method+path で紐付け） > 仕様の `example`/`examples` > スキーマの `example`**
で自動的に組み立てられる。必須パラメータ・必須リクエストボディ・2xx レスポンス定義のいずれかについて
値を解決できない operation は、**値を合成せず黙ってスキップ**される（失敗にはならない＝レポートに
「NG」は出ない）。

新しい operation を追加したとき、このテストで実際にカバーしたいなら次のいずれかが必要:

- 仕様側に `example`（パスパラメータ・クエリパラメータ・リクエストボディ）を書く、または
- `.claude/harness/config/ai-harness-openapi-smoke.yml` の `overrides` に、`method` + `path`
  （仕様の `paths` キーそのまま。例: `/users/{id}`）で紐付けたエントリを追加する。

どちらも無い operation は「スキップ」扱いになり、レポート上は成功でも失敗でもない（＝実は一度も
テストされていない）ことに注意する。判定は expected_status（既定は仕様の 2xx の最小値）とレスポンス
ボディの構造（`type`/`required`/`properties`/`items` のみ。`pattern`/`format` 等は対象外）。

## その他の設定

- `startup`: `base_url` が無応答のときだけ使うフォールバック起動コマンド（`cmd`・`wait`・`cwd`）。
  既に応答していれば実行されない。
- `overrides[].init`/`catch`/`final`: `cmd`（順次実行）・`sql`（クエリ結果のアサーション）による
  テスト前後のセットアップ／後始末。`sql` を使うならトップレベルの `sql`（接続設定。PostgreSQL/MySQL）
  が必須。

詳細は `ai-harness-openapi-smoke/README.md` を参照。
