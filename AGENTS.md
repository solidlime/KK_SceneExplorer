# Project guide line

## 1. プロジェクト概要
- 本プロジェクトのプラン作成、および回答は全て日本語で行います。
- Koikatsu Studio のシーン参照先にネットワークドライブ（NAS 等）を追加し、シーンをブラウズ・読み込み・保存できる BepInEx 5.x プラグインです。
- シーンブラウザ UI（ツリー・グリッド・サムネイル・ソート）、キャラ/衣装ブラウザ、保存転送（常にローカル保存 → 最後に読み込んだシーンのフォルダがネットワークなら自動転送）機能を持ちます。
- 技術構成: C# / .NET Framework 4.6 / Harmony / UnityEngine / BepInEx 5.x。ビルドは `build.cmd`（Release 出力は `bin\Release\KK_SceneExplorer.dll`）。

## 2. プロジェクト識別
- project: kk-scene-explorer

# Nous 記憶運用（.agent/ は使わない）

## セッション開始時（必須）
セッション開始時、ユーザーへの最初の応答の前に session-start スキルを実行し、nous 記憶から状態を復元する:
- `get_context` でペルソナ状態・直近サマリを取得
- `## プロジェクト識別` 節から `project: kk-scene-explorer` タグを取得
- `memory_search(tags=["project:kk-scene-explorer", "task_state"], top_k=3)` 等で作業状態を復元
- `memory_search(tags=["session_summary"], top_k=1, sort="updated_at")` で前回の内容を把握

## メモリ管理
- 重要情報・決定・作業完了は nous に記録。`project:kk-scene-explorer` タグ必須
- 状態変化時は `update_context` → `memory_create` の順で永続化
- `.agent/memory/MEMORY.md` / `.agent/handoff/HANDOFF.md` は使用しない（nous 記憶が代替）
- ローカルの自動メモリ機能（~/.claude/ 配下）は使用しない

## ハンドオフ管理
- セッション終了時の session_summary 生成（終了フック）が引継を代替する
- 手動引継が必要な場合は `memory_create(tags=["project:kk-scene-explorer", "task_state"])` で記録

## 仕様駆動開発（SDD）ルール
- コーディングや業務作業を開始する前に、必ず `.spec/` 配下の4ファイルを確認・更新すること
- 作業の順序：PLAN（目的確認）→ SPEC（要件確認）→ TODO（タスク確認）→ 実作業
- PLAN.md は人間の口頭メモ・自由記述であり、箇条書き・口語・断片的な内容で構わない
- PLAN.md を読んだら、そのまま実装に入らず、不明点をヒアリングしながら SPEC.md を作成・確定させること
- SPEC.md が確定してから TODO.md のタスク分解を行い、ユーザーの承認を得てから実作業を開始する
- 作業完了後は TODO.md の該当タスクにチェックを入れ、KNOWLEDGE.md に学びを記録する
- 仕様が不明確な場合は作業を開始せず、ユーザーに確認してから SPEC.md を更新する

## 品質ゲート

### トリアージ（3段階）
作業開始前にレベルを判定し、ゲートの重みを変える。

| レベル | 対象 | ゲート |
|--------|------|--------|
| 軽量 | 単一ファイル20行未満・機械的・1文で説明できるdiff | ビルド + 影響範囲確認のみ。REVIEW 省略可 |
| 標準 | 単一機能（数百行以内・ファイル分散 ≤5） | フルパイプライン（下記） |
| 本格 | 複数ファイル・アーキテクチャ/API/UI変更 | フル + 事前アーキテクチャ判断（#081） |

### パイプライン（検証ループ方式）
**EXPLORE** → **PLAN** → **IMPLEMENT** → **TEST**（検証ループ） → **REVIEW** → **GATE** → **COMMIT** → **PUSH**

| フェーズ | 担当 | やること | 通過条件 | 失敗時 |
|---------|------|---------|---------|--------|
| **EXPLORE** | #009 or orchestrator | コードベース探索、関連ファイル特定、依存関係把握 | 変更範囲が明確 | PLAN に進めない（再探索） |
| **PLAN** | orchestrator（委譲禁止） | 実装計画書の作成。影響範囲・ファイル一覧・テスト方針を明記 | 計画に具体性がある（ファイルパス・変更内容） | IMPLEMENT に進めない |
| **IMPLEMENT** | #011（複数ファイルは並列）or 直接 | 計画に従い実装。単一ファイル20行未満は直接、それ以外は #011 | コードが計画通りに書かれている | TEST に進めない（#011 に差し戻し） |
| **TEST** | 実行者 + 検証 | `build.cmd` で Release ビルド成功。UI 変更はゲーム実機確認（Studio でブラウザ表示・保存フローを確認） | ビルド成功 + 実機確認済み | max3 再試行 → 人間エスカレーション |
| **REVIEW** | #081（oracle・独立コンテキスト） | diff + 基準のみを見て correctness のギャップを反駁。スタイル好みは指摘しない。編集権限なし | **PASS（完全）**。それ以外は BLOCK | BLOCK → IMPLEMENT に戻る。BLOCK を上書き禁止 |
| **GATE** | orchestrator | 全項目を機械判定 | GATE 成立 | COMMIT 禁止 |
| **COMMIT** | orchestrator | `git add` + `git commit`。バグ修正は重大度に応じたプレフィックス | コミット成功 | — |
| **PUSH** | orchestrator | `git push` | プッシュ成功 | コンフリクト時は解決して再コミット |

### GATE = 機械的条件式
```
BUILD=pass AND REVIEW=PASS AND DIFF=単一目的 AND 禁止操作なし
```
- BUILD: `build.cmd`（Release）成功。C# 型チェックはコンパイルに包含
- REVIEW: #081 が PASS（完全）を判定。PASS 以外は BLOCK、BLOCK の上書き禁止
- 禁止操作: `git push --force` / `git commit --no-verify` / `DROP TABLE` / `DELETE FROM` なし。`rm -rf` は要警戒
- UI 変更: テストコードの成功のみでは完了としない。ゲーム実機（Studio）で表示・操作を確認必須

### 補足ルール
- 軽量トリアージ: ビルド + 影響範囲確認のみ実施し、限定版 GATE（BUILD）通過で COMMIT 可
- TEST 範囲: #011/#057 は変更ファイルのみ。orchestrator は全件（プロジェクト全体の回帰確認）
- 既存壊れテスト: 変更起因 → 即修正。既存障害 → #081 が修正/削除判断
- サブエージェント作業中に発見した副次的な問題は `## Drive-by Findings` 形式で報告。セッション終了時までに対応（修正 or 記録）
- バグ対応: 軽微（typo, null check, ＜10行）→ 即修正 `fix(drive-by):` 別コミット / 中程度 → `TODO(drive-by)` + 記憶 / 重大（アーキテクチャ, データ整合性）→ #081 にエスカレーション、判断なしでの修正禁止
