# Architecture decision record: GUI-first, user-space-only local harness

## Product boundary

利用者が触れるのはWPF GUIだけです。コマンドプロンプト、PowerShell、Ollama CLIを操作させません。GUIは `HttpClient` でOllamaのローカルAPIへ直接接続します。

## Installation boundary

配布アプリは自己完結型の単一EXEです。初回セットアップは公式Ollama portable ZIPをユーザー領域へ取得し、公式チェックサムを確認してからステージング展開します。公式Windowsインストーラーが `%LOCALAPPDATA%\Programs\Ollama\ollama.exe` に導入したユーザー用Ollamaも検出できます。任意のPATH、レジストリ、サービスは使いません。

```text
SafeLocalHarness.exe
  -> managed runtime または公式ユーザー用Ollamaを限定検出
  -> %LOCALAPPDATA%\LocalStudio\downloads  (一時取得)
  -> SHA-256検証
  -> %LOCALAPPDATA%\LocalStudio\runtime    (Ollama)
  -> hidden ollama serve on 127.0.0.1
  -> %LOCALAPPDATA%\LocalStudio\models     (モデル)
```

公式ユーザー用Ollamaを再利用する場合も、既存の共有 `127.0.0.1:11434` へ接続せず、LocalStudioが所有する動的loopbackポートの子プロセスとして開始します。既存モデルは `%USERPROFILE%\.ollama\models` から利用し、`OLLAMA_NO_CLOUD=true` でローカル専用にします。

起動したOllamaはkill-on-closeを設定したWindows Job Objectへ直ちに所属させます。アプリ終了時や予期しない親プロセス終了時にも、LocalStudioが所有するプロセスツリーだけを終了し、既存のユーザーOllamaには触れません。

## Agent boundary

長い会話履歴を実行状態として扱いません。永続的なexecution ledgerに、フェーズ、操作、対象、入力指紋、結果を記録します。同一対象・同一指紋の `NO_CHANGE` が2回続いた時点で操作を拒否します。

実装した編集セッションの実行フェーズ:

`Inspecting -> Planning -> Acting -> Verifying -> Complete`

変更前に対象ファイルだけをrun snapshotへ保存し、失敗時は `Restoring` を経て戻します。accepted baselineは検証を通過した状態だけを指し、run snapshotと分離しています。モデルやUIから `.localstudio` 配下へはアクセスできません。

## User feedback boundary

長時間処理を単一の待機状態として扱いません。計画処理はruntime、model、inference、validation、生成処理はprepare、generation、compile-check、candidate-rejected、manifest、files、verification、baselineへ分け、GUIに現在段階と経過時間を表示します。モデル速度は一定ではないため、設計中と生成中は開始時に固定した保守的な最大所要時間だけを示し、途中で残り時間を増減させません。ダウンロードだけは転送量から推定残り時間を併記します。

スタジオ入力は500msのデバウンス後と正常終了時に `%LOCALAPPDATA%\LocalStudio\state\studio-draft.json` へ原子的に保存します。プロジェクト一覧は `%LOCALAPPDATA%\LocalStudio\projects` と各 `localstudio.json` を読み取るだけで、一覧表示のためにプロジェクト内容を書き換えません。

## Verification boundary

テスト合格だけを完成条件にしません。少なくとも以下を組み合わせます。

- requirementsの静的検査
- プロジェクト固有テスト
- 変更対象の再読込とハッシュ確認
- Unicode破損検査
- 実行台帳上のループ・未検証変更検査
- 必要に応じた人間の確認

## Corporate environment behavior

AppLocker、WDAC、プロキシ、EDR等により実行または通信が拒否された場合、アプリは昇格や回避を試みません。ユーザー向けの理由と、情報システム部門へ渡せる診断情報だけを表示します。

## Generative implementation boundary

テンプレート外のローカルWebアプリは `GeneratedLocalWebApp`、ローカル専用Windowsアプリ／ゲームは `GeneratedWindowsExecutable` へ振り分けます。別用途の固定テンプレートへの置換は行いません。Windows経路は依頼固有の単一ファイルWinForms候補を生成し、安全検査、実コンパイル、コンパイル診断を使った最大4回の再生成を行います。失敗候補はステージング領域から正式プロジェクトへ移しません。

コード起因のコンパイル失敗と、コンパイラー不在等の環境起因失敗を区別します。前者だけをモデルへ戻し、後者は同じ生成を繰り返さずGUIへ説明します。許可済みの.NET Framework名前空間不足だけは決定的に補完できますが、危険API除去やゲーム仕様変更を文字列置換で行いません。

外部通信、アカウント連携、公開処理、実働GAS、実働Agentは、認証・権限確認・取消境界が未実装のままこの経路へ混ぜません。停止は「未知のアプリだから」ではなく、「明示許可なしに外部状態を変えるから」に限定します。

ローカルモデルが生成できるのは `<main>` 内の本文断片と `app.js` だけです。LocalStudioがCSP付き文書シェル、固定CSS、要件ID、保存先を所有します。候補は最大3回、サイズ・パス・UTF-8・制御文字・外部資源・危険API・アクセシビリティ・既知動作契約を決定的に検査します。

モデルによる要件レビューは画面IDとJavaScript識別子の根拠を要求しますが、同一モデルの自己評価であるため実動作保証には使いません。Windows生成は静的安全検査と実コンパイルを独立信号として追加します。いずれの新規実装も `STRUCTURAL_PASS` / `structural-preview` と記録し、実ブラウザまたは利用者による操作確認後にのみ完成度を引き上げます。復元用スナップショットも `accepted` baselineとは区別します。

## User verification boundary

生成アプリにはLocalStudio所有の `verification-plan.json` を付与します。プロジェクト画面は要件ごとの短い確認手順を表示し、利用者が実際にアプリを操作した結果を `manual.json` へ保存します。全要件の確認が揃った場合だけ `user-verified` とし、失敗が1件でもあれば `verification-failed`、一部だけなら `partial` として扱います。

確認証拠はWebでは `index.html`、`styles.css`、`app.js`、Windowsでは `Program.cs`、`app.manifest`、生成EXEに加え、共通の `requirements.json` と `verification-plan.json` のSHA-256に結び付けます。いずれかが変化した場合、以前の確認結果は `stale` となり、全要件を未確認へ戻します。検証台帳はプロジェクトルート外、ジャンクション、シンボリックリンクを拒否し、原子的な一時ファイル置換で保存します。
