# ダンおに譜面エディタ (IDE / ITTN-DANONI-EDITOR)

[danoniplus](https://github.com/cwtickle/danoniplus) 互換の譜面ファイル(dos.txt)を作成・編集するための、Windows向けデスクトップ譜面エディタです。1ウィンドウ=1曲(複数難易度タブを内包)構成で、既存のFUJIエディタ・SKBエディタ・dos.txtからのインポートにも対応しています。

## 主な特徴

- **24キー種対応**: 5key〜23keyまで標準テンプレートを収録。テンプレート編集ウィンドウでオリジナルキー種の作成・キーパターンの追加も可能
- **マウス/キーボード両対応の編集**: スマートツールによるマウス編集と、SKB操作モード準拠のキーボード入力の両方をサポート
- **インポート**: FUJIエディタ形式・SKBエディタ形式・dos.txt単体・自形式タブファイルからの取り込みに対応(ドラッグ&ドロップで自動判定)
- **異なるキー種間のコピー&ペースト**: コピーマネージャーでレーン対応を指定すれば、キー種の異なる難易度タブ間でもノート・フリーズを貼り付け可能
- **プレイテスト/目視テスト/プレイ画面プレビュー**: 判定・コンボ表示付きの本格的なプレイテストから、DAWライクなループ再生、判定なしの仮想プレビューまで
- **ゲージ設定・ゲージ計算機**: customGauge/difDataゲージの編集と、必要達成率・逆算計算をサポート
- **レーン入替マクロ・歌詞レーン(word_data)**: 反復作業を効率化するマクロ機能と、歌詞表示データの編集に対応
- **プラグイン対応**: 外部制作者が独自パネル・オーバーレイ描画・簡易編集APIを追加できるプラグイン機構を搭載(詳細は [`plugin-sdk/`](plugin-sdk/) 参照)
- **Undo/Redo・自動保存**: 編集操作は基本的に全てUndo可能。クラッシュ復旧用の自動保存にも対応

## ドキュメント

- 操作方法の詳細は [`docs/readme.html`](docs/readme.html) を参照してください
- プラグイン開発は [`plugin-sdk/README.md`](plugin-sdk/README.md) から始められます

## ビルド・実行

.NET 8 SDKが必要です。

```
dotnet build src/DanoniEditor.App          # ビルド(コンパイル自体はLinux上でも可能、実行はWindowsのみ)
dotnet publish src/DanoniEditor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

ビルド番号の自動インクリメント込みでpublishしたい場合は、`tools/bump_and_publish.ps1`(または`tools/bump_and_publish.mjs`)を使うと1コマンドで完結します。

```
.\tools\bump_and_publish.ps1
```

### テスト

```
dotnet test tests/DanoniEditor.Core.Tests
```

## 動作環境について

配布されたexeを実行すると、コード署名が付いていないため「発行元不明」のWindows SmartScreen警告が表示されることがあります。個人配布の無償ツールでは一般的な挙動で、ウイルス等を意味するものではありません(詳細は`docs/readme.html`参照)。

## ライセンスについて

imgフォルダに存在する画像は「Dancing☆Onigiri (CW Edition)」より使用させていただいております。(MIT License)
https://github.com/cwtickle/danoniplus
当エディタについては特に設けておりませんが、良識の範囲でご利用ください。

## クレジット

2026 K-AN / ITTN.EXE
