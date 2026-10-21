# LocalStudio research and design notes

更新日: 2026-09-09

## UI

Apple Human Interface GuidelinesのHierarchy、Harmony、ConsistencyをWindows向けWPFへ翻訳した。コンテンツを主役にし、広い余白、明確な文字階層、丸みのあるサーフェス、一画面につき一つの主要操作、状態伝達に限定したアクセントカラーを採用する。

- https://developer.apple.com/design/human-interface-guidelines
- https://developer.apple.com/design/human-interface-guidelines/layout
- https://developer.apple.com/design/human-interface-guidelines/buttons
- https://developer.apple.com/design/human-interface-guidelines/accessibility

Appleの画像・ロゴ・固有UI資産は使用せず、原則だけをWindowsの標準操作へ適用する。

## Agent architecture

2026年のcoding-agent研究が示す、状態管理、権限、検証、観測性、エラー処理をモデル本体から分離する。LocalStudioではPresentation、Application、Domain、Infrastructureの依存方向を固定し、自動テストで逆依存を拒否する。

- https://arxiv.org/abs/2608.13867
- https://arxiv.org/abs/2608.10934
- https://arxiv.org/abs/2605.18747
- https://arxiv.org/abs/2608.00808

会話入力を即時実行せず、`conversation -> validated blueprint -> confirmed local project -> verified implementation -> explicit external publish` の順に進める。

## Requirement fidelity and executable evaluation

SWE-RPGは要求明確化・計画・コード生成を連続した課題として扱い、暗黙要件の回収を主要な失敗要因として報告する。SWE-CIは一回限りの修正成功ではなく、反復変更下の保守性を評価対象にする。LocalStudioでは、設計文の自己申告ではなく、成果物の形式、決定的契約、独立検証、人による操作確認を分けて保存する。

- https://arxiv.org/abs/2608.09072
- https://arxiv.org/abs/2603.03823
- https://arxiv.org/abs/2601.03731

出力形式も暗黙推定だけで確定せず、WebアプリとWindows EXEを明示選択にし、推奨と実際の選択を同時に表示する。内部要件IDは成果物UIへ混ぜず、検証記録側に保持する。

## Google Apps Script

ローカル生成にNode.jsやclaspを必須としない。GASプロジェクトはまずユーザー領域へ生成し、外部連携はApps Script REST APIを利用する。OAuthはWindowsデスクトップ向けloopback flowを予定し、アカウント認可・更新・公開は個別のユーザー確認を必要とする。

- https://developers.google.com/apps-script/api/reference/rest
- https://developers.google.com/identity/protocols/oauth2/native-app
- https://developers.google.com/apps-script/api/how-tos/enable

## Non-admin invariant

LocalStudioはUAC昇格、Windowsサービス、Program Files、HKLM、システムPATHを使用しない。必要物は `%LOCALAPPDATA%\LocalStudio` に限定する。企業ポリシーによる拒否は回避せず、安全停止と診断表示で扱う。

生成するWindows EXEにも `requestedExecutionLevel=asInvoker` と `uiAccess=false` を明示的に埋め込む。これはWindowsの最小権限ガイダンスに従い、インストーラー判定による意図しない昇格も避けるためである。

- https://learn.microsoft.com/en-us/windows/win32/secbp/running-with-administrator-privileges
