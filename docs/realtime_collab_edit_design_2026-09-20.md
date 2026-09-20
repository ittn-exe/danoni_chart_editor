# 共同編集(マルチプレイ編集)機能 設計メモ (2026-09-20時点)

> このドキュメントはブレインストーミングの内容を整理したドラフトです。
> 「TBD」とある項目は未確定・要追記です。実装着手前に本体設計との整合を再確認すること。

## 1. 目的・想定利用シーン

- 複数人が同じ譜面プロジェクトをネットワーク経由でリアルタイムに共同編集できるモードを追加する。
- 想定人数: 3〜5人程度の少人数チーム。
- 想定利用形態: Discord等のボイスチャット/テキストチャットで常時連絡を取り合いながら、リアルタイムに同時編集する。
- 接続経路: インターネット越し・各自ポート開放前提(LAN限定は想定しない)。
- 中央サーバーは置かない。エディタ単体(exe)で完結する構成とする(外部SaaS・共有サーバーへの加入は前提としない)。
- 「持ち帰って一人で吟味したい」「相手の都合がつかない」等の理由で、共同作業の合間に個人で編集する時間も自然に発生する前提とする(5章参照)。

## 2. 全体アーキテクチャ

### 2.1 権威モデル(確定)

- **ホスト権威モデル**を採用。参加者のうち1人が「ホスト」となり、そのPC上のプロジェクトデータを正本として扱う。
- 他の参加者(ゲスト)は、自分のクライアント上にも同じプロジェクトデータの写しを保持し、ローカルで通常通り編集操作(Undo/Redoを含む)を行う。
- **Undo/Redoは各クライアントでローカル完結**とする(仕様書13章のUndoStackはそのまま流用、ネットワーク同期の対象にしない)。他人の操作を自分のUndoで巻き戻すことはできず、逆に自分のUndoをネットワークで特別扱いする必要もない。Ctrl+Zの結果として自分の手元データが変化し、その変化が通常の編集と同様にホストへ送られる、という扱いに統一する。
- なお、この権威モデル・Undo方針は「共同タイムライン」に対してのみ適用される。「個人タイムライン」は常に完全ローカルで、ネットワーク同期の対象外(5章参照)。

### 2.2 同期の単位: セル状態差分(確定)

- ネットワークでやり取りする最小単位は、「操作(コマンド)」ではなく「結果としての状態」とする。
- 通常ノート/フリーズ用の基本形:
  - `(タブ番号, レーン番号, tick位置) → 状態値`
  - 状態値: `0=空 / 1=通常ノート / 2=フリーズ始点 / 3=フリーズ終点`
  - 移動は「旧位置を0、新位置を1〜3」という2件のセル更新として表現し、専用の「移動」メッセージは設けない。
- フリーズノートは内部的に「開始tick+長さ」の1オブジェクトとして管理されているため、始点(2)・終点(3)は分離した2件のセル更新ではなく、`(タブ番号, レーン番号, 開始tick, 終了tick)` を1セットにした1件のメッセージとして送る(始点だけ届いて終点が届かない、という半端な状態が一瞬相手画面に表示されるのを避けるため)。
  - **実装メモ(2026-09-20)**: 上記の理由により、実際のワイヤ上の`NoteCellChangedMessage`は`Empty(0)`/`Note(1)`の2値のみを持つ(通常ノート専用)。フリーズの追加・更新・削除は常に`FreezeChangedMessage(タブ番号, レーン番号, 開始tick, 終了tick?)`側で表現し(終了tickがnullなら削除)、フリーズの移動も「旧開始tickを終了tick=nullで削除」+「新しい開始/終了tickで追加」の2件として表す。概念上の状態値2/3はこの2メッセージ種の組み合わせとして実現されており、単一の列挙値としては送出しない。

#### 2.2.1 イベント列(speed/boost/BPM)(確定)

- レーンではなく「イベント用の1列」にtickごとの数値が乗る構造。ノートの0〜3のような整数enumとは異なり、数値そのものに「0」という有効な値があり得る(変速0倍・boost0等)ため、ノートのように「0=空」とは決め打ちできない。値そのものとは別に「削除された」ことを表す印(null)を明示的に持たせる。
- `(タブ番号, 列種別[Speed|Boost|Bpm], tick) → 数値 | null(削除)`

#### 2.2.2 マーカー(見出しコメント・拍子情報)(確定)

- tickに紐づく単一の情報のため、イベント列と同じ形を踏襲する。
- `(タブ番号, tick) → {コメント, 拍子情報等} | null(削除)`

#### 2.2.3 ヘッダー・プロジェクトメタ情報(確定)

- dos_headerの各種パラメータのような「1プロジェクト(またはタブ)につき1つの設定値」はセルという考え方に馴染まないため、項目名ベースの差分とする。
- `(対象[プロジェクト全体 or タブ番号], 項目名) → 値`

#### 2.2.4 構造そのものの変更(タブ追加・削除・並び替え・テンプレート変更)(確定)

- 「値が変わる」のではなく「一覧の形そのものが変わる」性質のため、無理に他の形へ押し込めず、専用のメッセージ種として個別に用意する。
- `AddTab(位置, タブデータ)` / `RemoveTab(タブ番号)` / `ReorderTabs(新しい並び順)` / `ChangeTemplate(タブ番号, 新テンプレートID)`

#### 2.2.5 フレーム情報モード(7.6章)は同期対象外(確定)

- 仕様上「ユーザーが自分の環境で一時的に切り替える表示・計算モード」であり、ノートのtick自体は不変のままという設計のため、ネットワーク同期の対象にはしない(各自のローカルな作業補助のまま)。

### 2.3 通信の非対称構成(確定)

- **ゲスト→ホスト**: 変更のあったセルのみをピンポイントで送信する。
- **ホスト→ゲスト**: ホスト側の正本(自分の編集+各ゲストから届いた差分を反映済みのもの)を、プロジェクト単位でまとめて配信する。
  - v1では「変更を受け取るたび毎回プロジェクト全体を配信する」という最も単純な方式を採用する。譜面データはテキストベースで軽量なため、通信量・頻度の面で問題は生じない想定。
  - 編集が集中して負荷が高くなった場合の調整弁として、「一定回数ごと」「一定時間ごと」の間引き(デバウンス)を後から追加できるようにしておく(v1では不要)。
- この非対称構成により、「後から届いたセル値で上書きする」だけで自然に後着優先(last-write-wins)が成立し、操作の衝突判定・却下・巻き戻し通知といった複雑な処理が不要になる。

### 2.4 既知のトレードオフ

- ゲストが送った直後の差分がまだホストの配信に取り込まれていないタイミングで、ホストからの(その差分をまだ含まない)全体状態が届くと、ゲスト画面が一瞬古い状態に見える可能性がある(直後の次回配信で解消される)。
  - v1では素朴に許容する。体感で問題になるようであれば、「自分が送った差分が確認できるまでは、届いた全体状態にその差分を上書きし直す」補正を追加する。

## 3. 接続・セッション管理

### 3.1 通信方式

- 生のTCPソケットを使用。メッセージは `[4バイト長][JSON本文]` 形式で区切る。
- JSONシリアライズは既存の `System.Text.Json`(プロジェクト保存で使用中)をそのまま流用する。
- ホストはTCP待ち受け(ポート番号は設定可能)、ゲストはホストのIP:ポートへ接続する役割分担。

### 3.2 参加フロー

1. ゲスト→ホスト: 挨拶(表示名・希望カラー・プロトコルバージョン)
2. ホスト→ゲスト: 参加者ID発行、現在の参加者一覧
3. **セッション新規立ち上げ時のみ**: ホスト・ゲスト全員が持つ「共同タイムラインのタイムスタンプ」を比較する入札を行う(5.3節参照)。一番新しいものを持つ参加者(ホストとは限らない)がその内容をホストへアップロードし、ホストはそれを正本として採用する。
4. ホスト→ゲスト: 採用された共同タイムライン全体(既存の `ProjectSerializer` でJSON化したもの)を送付
5. 以降、通常運用(2.3節)に移行。セッション立ち上げ後に途中参加する人は、3の入札を行わず単純に「今ホストが持っている最新状態」を受け取るだけでよい。

### 3.3 切断・再接続

- TCP切断/一定時間応答なしをホストが検知し、参加者名簿から除外、残りの参加者へ通知する。
- 再接続は新規参加と全く同じ手順(挨拶→全体スナップショット)で行う。差分だけの復旧は行わない(単純さを優先)。

### 3.4 ホストが落ちた場合の扱い(確定方針)

- ホスト→ゲストの配信を「変更のたび毎回プロジェクト全体を送る」方式にしているため、各ゲストは常時ほぼ最新の状態を保持している。この性質により、特別な引き継ぎ処理を作り込まなくても復旧しやすい設計になっている(IRCのnetsplit再同期、オンラインゲームのホストマイグレーションいずれも「普段から全員に状態を配っておく」ことが復旧を容易にする、という共通の教訓がある)。
- ホスト切断を検知したゲストは、勝手にローカルで編集を続けず(分岐した状態を後でマージする複雑さを避けるため)、「ホストとの接続が切れました」と表示して待機する。
- **復旧パターンA(同じ人が再開)**: 元ホストが再度待ち受けを開始し、ゲストは通常の参加フローで再接続する。特別なコードパスは不要。
- **復旧パターンB(別の人が引き継ぐ)**: 自動選出は行わず、「このPCでホストを引き継ぐ」ボタンを人が押す方式とする(自動化すると、新ホスト候補のポート開放が済んでいない場合にかえって混乱するため)。引き継いだ人は、その時点で自分が保持している(ほぼ最新の)状態をそのまま新しい正本として使う。

### 3.5 保存タイミング(確定)

- 参加者は誰でも、好きなタイミングでローカルにプロジェクトファイルを保存できる(通常のCtrl+S保存がそのまま使える)。
- **セッション終了時は必ず保存を行う**(確定)。終了トリガー(切断検知/明示的な退出操作/アプリ終了)のいずれでも、共同タイムライン+個人タイムラインの現在状態をディスクへ書き出す。
- **セッション中の定期自動保存も行う**(確定)。異常終了(アプリクラッシュ・PCの電源断等、正常終了フローを通らないケース)への保険として、以下の方針とする。
  - 対象は**ホスト側のみ**(正本を持つのはホストのため。ゲストは再接続時にホストから貰い直せば復旧できる)。
  - 間隔は**5分**を既定値とする。
  - バックグラウンドで無音実行し、ユーザー操作を妨げない(通知も最小限)。

## 4. 接続性(ポート開放・CGNAT)への対応(確定・一部TBD)

### 4.1 基本方針(確定)

- エディタ単体で完結させる方針とし、Tailscale/ZeroTier等の外部サービスへの加入は前提としない。
- CATV回線等、CGNAT(キャリアグレードNAT)配下の環境では、通常のポート開放が機能せずホストになれない場合がある。開発メンバーに実際にCGNAT環境の参加者がいるため、この対応は後回しにせず早期に組み込む(7章のフェーズ2)。

### 4.2 CGNAT対応の設計: 仲介ヘルパー + TCP同時オープン(確定・一部TBD)

UDPでの穴あけ(hole punching)は、TCPが担っている順序保証・再送処理まで自前で実装する必要が生じ工数が大きく膨らむため、**TCPの構成を維持したまま繋ぐ「TCP同時オープン」方式**を採用する。

- **役割定義**:
  - 「ホスト」(共同タイムラインの正本を持つ参加者、2.1節)とは別に、「仲介ヘルパー」(普通の回線で外部から到達可能な参加者)という役割を新設する。
  - ホスト自身が普通の回線であれば、ホストが仲介ヘルパーを兼ねる(この場合、追加の手順は一切発生しない)。
  - ホストがCGNAT配下の場合のみ、別の到達可能な参加者が仲介ヘルパーを一時的に引き受ける。
- **仲介ヘルパーの機能**: エディタの通信機能内に簡易STUN(受け取ったパケットの送信元IP:ポートをそのまま返すだけのエコー応答)を持たせる。別ソフトの導入は不要。
- **接続確立の手順**:
  1. CGNAT側のホスト・ゲストの双方が、仲介ヘルパーへ「自分が外からどう見えているか」を問い合わせる(エコー要求)。
  2. 仲介ヘルパーが、双方の外向きIP:ポート情報を相手側へ中継する。
  3. 仲介ヘルパーが両者へ同期の合図を送り、双方がほぼ同時に相手の外向きアドレスへTCP接続を試みる(TCP同時オープン)。
  4. 接続が確立すれば、以降は2〜3章のプロトコルがそのままそのTCP接続上で動作する。仲介ヘルパーの役目はここで終わる。
- **失敗時の扱い**: symmetric NAT配下同士など、原理的に同時オープンが成立しない組み合わせの場合は失敗として扱い、「別の参加者に仲介ヘルパーを頼む」か「既知の制約として受け入れる」のいずれかとする。
- **前提条件**: この方式は、セッション内に少なくとも1人、通常に到達可能な参加者(仲介ヘルパー候補)がいることを前提とする。参加者全員がCGNAT(特にsymmetric NAT)の場合は、この方式でも解決できない。
- **TBD**: 仲介ヘルパー候補が複数いる場合の選び方(自動選定はせず、ユーザーが手動で「誰に頼むか」を
  Discord等で相談して決める運用のまま、2026-09-20時点でも未対応・意図的に対応しない)。

**2026-09-20 実装済み**: エコー要求/中継/同期合図の具体的なメッセージ形式(`RendezvousHelloMessage`/
`RendezvousPeerAddressMessage`/`RendezvousGoMessage`/`RendezvousFailedMessage`)、待ち合わせの合言葉
(SessionCode、Discord等で口頭合わせする短い文字列)、待機タイムアウト(既定3分)、同時オープンの
試行タイムアウト(15秒、200ms間隔でリトライ)を確定・実装した。詳細は9章参照。

## 5. 個人タイムライン/共同タイムラインの分離設計(確定・一部TBD)

「合作中でも持ち帰って一人で吟味したい」「相手の都合がつかない時は個人で進めたい」という利用実態に対応するため、プロジェクトファイル内に2系統のタイムラインを持たせる。イメージとしてはGitのブランチに近く、**共同タイムライン=チームの共有ブランチ、個人タイムライン=自分だけの作業ブランチ**として捉えると理解しやすい。

### 5.1 基本ルール(確定)

- プロジェクトファイルは「共同タイムライン(+最終更新タイムスタンプ)」と「個人タイムライン」の2系統を保持する。
- **セッション未接続時(オフライン作業時)**: 個人タイムラインのみ編集可能。共同タイムラインは凍結され、表示はされるが編集不可。
- **セッション接続時(ホスト/ゲスト問わず)**: 共同タイムラインが編集可能になり、2章で設計した同期(セル差分のやり取り)はすべて共同タイムラインに対して行われる。個人タイムラインはこの間もネットワークに一切乗らない、完全ローカルな領域のまま。

### 5.2 UI: 分割ビューの活用(確定・一部TBD)

- 既存の「分割ビュー(左右2ペイン)」機能を流用する。左ペイン=個人タイムライン(常に編集可、通常のオフライン作業と同じUI)、右ペイン=共同タイムライン、という固定割り当てとする。
- 「共同タイムラインを表示」ボタンで右ペインの表示をON/OFFする。
- **未接続時の右ペイン**: 編集不可。オブジェクトの選択のみ可能(下記D&Dのコピー元/コピー先として使うため)。
- **接続時の右ペイン**: 通常のキーボード/マウス操作で直接編集可能(2章の同期対象そのもの)。

### 5.3 個人タイムライン⇔共同タイムライン間のやり取り(確定・一部TBD)

- **共同→個人へのコピー**: 共同タイムライン上でオブジェクトを選択し、個人タイムライン側へドラッグ&ドロップすることで、チームの現在の進捗を土台にした個人作業を開始できる。
- **個人→共同への反映**:
  - 個人タイムラインで作ったオブジェクトを、共同タイムライン側へドラッグ&ドロップする。
  - 接続中であれば、共同タイムライン上で直接編集する(通常の共同編集操作そのもの)。
- 個人タイムライン側で共同タイムラインへの反映操作(D&D)を行うと、その時点でローカルの共同タイムラインのタイムスタンプが更新される。これにより、次回セッション接続時の入札(3.2節)で、この更新内容が採用されうる状態になる。
- **既知のリスク(TBD・要検討)**: タイムスタンプ入札は「中身の充実度」ではなく「いつ更新したか」だけで勝敗が決まる単純な仕組みのため、個人作業中にタイムスタンプを更新した結果、その間にチームが集めていたより新しい共同タイムラインの進捗を意図せず上書きしてしまう可能性がある。対策として、入札で「今まで使っていたものと異なる参加者の状態が採用されようとしている」場合は、採用前に「〇〇さんの状態(最終更新: △△)を採用します。よろしいですか？」といった確認ダイアログを挟むことを推奨する。**この確認ダイアログの採用可否・具体的な文言は未確定。**

### 5.4 キーボード操作: 左右ペインのフォーカス切り替え(確定・一部TBD)

- 既存のショートカット一覧(`keyboard_shortcuts.md`)を確認した結果、**Tabキー(修飾なし)が未使用**であったため、これを左右ペインのキーボード操作対象切り替えに割り当てる。
  - 「共同タイムラインを表示」中のみ有効化する(通常の単一ペイン作業時はTabキーに意味を持たせない)。
  - どちらのペインに操作が向いているかを、枠線の色等で視覚的に示す。
- 右ペイン(共同タイムライン)にフォーカスがある間の挙動:
  - **未接続時**: ↑↓・Space・B・←→によるカーソル移動とオブジェクト選択は可能。レーン入力キーでの配置・Backspaceでの削除は無効(読み取り専用の原則を維持)。
  - **接続時**: 通常のキーボードモードと全く同じに振る舞う(配置・削除ともに可能で、そのままネットワークへ送信される)。
- これは、既存の「分割ビューの副ペインは特殊モード(範囲選択モード等)の対象外とする」という決定(将来実装候補6)を、共同タイムライン用途に限って部分的に見直すものである。範囲選択モード/StartNumber編集モード等、他の特殊モードの副ペイン対応は引き続きスコープ外のまま。
- **実装詳細(TBD)**: `MainWindow_PreviewKeyDown`/`HandleKeyboardModeKey`側でのTabキーのハンドリング方法、フォーカス状態の保持場所(ChartCanvas単位かEditorDocument単位か)は未確定。

## 6. プレゼンス表示(他ユーザーの選択状態の可視化)(確定・一部TBD)

複数人が同時に共同タイムラインへ触れる以上、「今誰がどこを触っているか」が見えないと事故(同じ箇所への同時編集)が起きやすくなる。ロックはかけず、代わりに視覚的な手がかりで衝突を未然に減らす方針とする。

### 6.1 同期する情報(確定)

- 各参加者の**現在の選択オブジェクト集合**(タブ番号+選択中のセル座標一覧)のみを対象とする。
- カーソル位置・スクロール位置(ビューポート)の共有は行わない(v1では優先度低のため見送り)。
- プレゼンス情報は共同タイムラインの実データではないため、**プロジェクトファイルへは保存しない**(セッション中のみ有効な一時情報。`EditorDocument.NotifyChanged(markModified: false)`と同様の「データを変えない変化」として扱う)。

### 6.2 配信方式(確定)

- 選択状態が変わるたびに、ゲスト→ホストへ即座に送信し、ホストは受信した内容をそのまま(検証なしで)他の全参加者へ転送する。
- 2.3節のプロジェクト全体配信とは別の、軽量な専用メッセージとして扱う(選択変更はクリックのたびに発生し得るため、重いプロジェクト全体配信に相乗りさせない)。

### 6.3 表示方法(確定・一部TBD)

- 参加者ごとに識別色を割り当てる(参加時に希望色を申告、重複時はホストが空いている色へ自動振替)。
- 他の参加者が選択中のオブジェクトを、その参加者の色で枠線ハイライト表示する。
- 複数人が同じオブジェクトを選択した場合は、両方の色を重ねて表示する(排他制御はせず、あくまで表示上の目印とする)。
- 常時参加者一覧(名前+色+接続状態)を画面の一角に表示する。
- 5.2節の分割ビューとの関係: プレゼンス表示は共同タイムラインが表示されているペイン(単一ビュー時はそのまま、分割ビュー時は右ペイン)にのみ描画する。
- **TBD**: 参加者一覧の具体的な設置場所(上パネル/専用サイドバー等)、識別色パレットの初期セット。

## 7. 段階的な実装案(TBD)

1. **フェーズ1(MVP)**: ノート/フリーズのセル差分のみ対応、直結TCP(普通に到達可能な環境同士)で動作確認。個人/共同タイムラインの分離は未対応(共同タイムラインのみで検証)。
2. **フェーズ2: CGNAT対応**(4.2節): 仲介ヘルパー+TCP同時オープンによる接続確立。開発メンバーの実環境(CATV)で必要になるため前倒しで着手する。
3. **フェーズ3**: インターネット越し接続の本格運用、変速/加速/BPM等イベント列の差分対応、プレゼンス機能(6章)。
4. **フェーズ4**: 個人/共同タイムラインの分離、分割ビューの活用、タイムスタンプ入札、D&Dによる反映操作。
5. **フェーズ5**: ホスト引き継ぎボタンの実装、セッション終了時保存(実施済み方針の実装)、接続状況の可視化。

## 8. 未決定事項まとめ

- 変速/加速/BPM/マーカー/ヘッダー/タブ構造変更のメッセージ形式は大枠確定(2.2.1〜2.2.4節)。実際のJSONスキーマ(フィールド名・型)の詳細設計は今後の実装時に詰める。
- プレゼンス機能の参加者一覧の設置場所・識別色パレット(6.3節)。
- 仲介ヘルパーのメッセージ形式・同時オープンのタイムアウト/リトライ・候補選定方法(4.2節)。
- タイムスタンプ入札時の上書き確認ダイアログの採用可否・文言(5.3節)。
- 左右ペインフォーカス切り替えの実装詳細、フォーカス状態の保持場所(5.4節)。

## 9. 実装状況(2026-09-20)

フェーズ1(MVP)の通信基盤・セル差分同期エンジンに着手した。

- 新規プロジェクト `src/DanoniEditor.Collab/`(`DanoniEditor.Core`のみ参照、`DanoniEditor.Editing`/UIとは疎結合)
  - `Protocol/CollabMessage.cs`: メッセージ型一式(Hello/Welcome/Snapshot/ParticipantJoined/ParticipantLeft/NoteCellChanged/FreezeChanged)、`CollabProtocol`定数(バージョン・既定ポート47621・最大メッセージサイズ)
  - `Transport/CollabConnection.cs`: `[4バイト長(ビッグエンディアン)][UTF-8 JSON]`形式のフレーミング送受信(3.1節)
  - `Session/CollabHost.cs` / `Session/CollabGuestClient.cs`: TCP待受・接続・参加フロー(3.2節)、切断検知と離脱通知(3.3節)、識別色の自動割当(6.3節の前段)
  - `Sync/CellDiffApplier.cs`: セル差分メッセージの生成・`ChartProject`への適用(EditorDocument/UndoStackとは独立)
  - `Sync/SnapshotSync.cs`: 既存`ProjectSerializer`を用いたスナップショットの送受信
- テストは既存の慣例に倣い `tests/DanoniEditor.Core.Tests/Collab/` に追加(`DanoniEditor.Editing`のテストも同Testsプロジェクトに同居している既存パターンを踏襲):
  - `CellDiffApplierTests.cs`: セル差分の生成・適用・範囲外インデックスの例外・スナップショット往復
  - `CollabHandshakeTests.cs`: ループバックTCPでの参加フロー・メッセージ到達・ブロードキャスト除外・離脱通知の結合テスト
- `DanoniEditor.sln`・`tests/DanoniEditor.Core.Tests.csproj`へ新規プロジェクトを追加済み。

**2026-09-20 追記: WPF側配線(簡易版)完了**

ユーザー確定仕様により、真のセル差分(設計メモ2.2節)ではなく「簡易版:双方向スナップショット送信」で
配線した(EditorDocument側に「どのセルが変わったか」を追える仕組みが無く、それを実現するには
EditActions.cs(79KB規模)側への広範な改修が必要になり工数がかさむため。CellDiffApplierは
将来の差し替えに備えてそのまま温存)。

- 新規 `src/DanoniEditor.App/Collab/`
  - `CollabSessionController.cs`: MainWindowから独立した1セッションぶんの実行時状態管理。
    ホスト/ゲストいずれか一方のみ。ローカル編集(`EditorDocument.Changed`)を500msデバウンスして
    `SnapshotMessage`として送信、受信したスナップショットは`SnapshotSync.ApplySnapshotInPlace`
    (新規、リフレクションで`ChartProject`の全publicプロパティを総入れ替え——「本体が正」原則に
    則り、将来ChartProjectにプロパティが増減してもこちら側の修正が不要になるようにしている)で
    既存の`ChartProject`インスタンスへ反映する。適用中はローカル編集扱いにしないガード
    (`_applyingRemoteSnapshot`)で送信ループを防止。公開イベントは全てDispatcher経由でUIスレッド上に
    marshalしてから発火するため、呼び出し側(MainWindow)はスレッドを意識しなくてよい。
    ホストは参加者からスナップショットを受け取るたびマージして全員へ再配信する
    (「常に全員へ全体状態を配る」設計、2.3節)。
  - `CollabHostStartDialog.cs`/`CollabJoinDialog.cs`: 表示名・ポート・接続先入力の最小ダイアログ
    (`SimplePrompt.cs`と同じXAML無しのコード生成ウィンドウの作法に倣った)。
- `Sync/SnapshotSync.cs`(Collabプロジェクト側)へ`ApplySnapshotInPlace`を追加(上記)。
- `MainWindow.xaml`: 新規トップレベルメニュー「共同編集(_C)」(ホストを開始/セッションに参加/切断)、
  ステータスバーに接続状態表示(`CollabStatusText`)を追加。
- `MainWindow.xaml.cs`: `_collab`フィールド・3つのメニューハンドラ・`OnClosing`でのベストエフォート切断を追加。
  受信スナップショット適用後は既存の`InvalidateChartViews()`(2026-07-26整備済みの再描画一元化メソッド)
  ＋ミニマップ再描画を呼ぶことで画面反映する。
- `DanoniEditor.App.csproj`へ`DanoniEditor.Collab`へのProjectReferenceを追加。
- ビルド確認: `dotnet build src/DanoniEditor.App/DanoniEditor.App.csproj`(クラウド側検証環境、
  `-p:EnableWindowsTargeting=true`)→ エラー0件で成功。

**未着手(次のステップ)**:
- 動作確認(実機・複数プロセスでのホスト/ゲスト接続テスト、および実際のCGNAT環境での仲介ヘルパー疎通確認)。
  今回はビルド確認・ループバック結合テストのみで、実際にウィンドウを起動しての疎通確認や、
  本物のCGNAT環境での検証はできていない(相手の都合が必要なため、ユーザー確定方針により後回し)。
- 真のセル差分実装(EditActions側の変更範囲報告への改修、着手する場合は工数見積りから)
- 5章の個人/共同タイムライン分離、6章のプレゼンス機能(参加者一覧のUI表示は現状ステータスバーの
  人数表示のみ、詳細な一覧UIは未実装)

**2026-09-20 追記: 仲介ヘルパーのWPF側UI配線**

`src/DanoniEditor.App/Collab/`へ追加:
- `RendezvousHelperStartDialog.cs`/`RendezvousJoinDialog.cs`/`RendezvousAcceptDialog.cs`:
  仲介ヘルパー機能の3つの役割それぞれの最小ダイアログ(CollabHostStartDialog.cs等と同じ
  XAML無しのコード生成ウィンドウの作法)。
- `CollabSessionController`へ`JoinViaRendezvousAsync`(仲介ヘルパー経由でホストへ参加)・
  `AcceptViaRendezvousAsync`(ホストとして、仲介ヘルパー経由で参加者を1人受け入れる)を追加。
  いずれも内部で`RendezvousClient.EstablishAsync`を呼んで確立したTCP接続を、通常の直接接続と
  全く同じ経路(`CollabGuestClient.ConnectViaExistingSocketAsync`/`CollabHost.AcceptExternalClient`)
  へ合流させる。
- `MainWindow`の「共同編集」メニューへ「仲介ヘルパー機能(CGNAT対応)」サブメニューを追加
  (仲介ヘルパーとして待機する/停止、仲介ヘルパー経由でホストへ参加する、仲介ヘルパー経由で
  参加者を受け入れる)。仲介ヘルパーとして待機する機能は共同編集セッション本体とは独立した
  ライフサイクル(`_rendezvousHelper`フィールド)で管理し、`OnClosing`でのベストエフォート後始末も追加。
- ビルド確認: `dotnet build src/DanoniEditor.App/DanoniEditor.App.csproj` → エラー0件。
  `DanoniEditor.Core.Tests`のCollab/Rendezvous関連テスト18件も引き続き全て成功。

**2026-09-20 追記: 仲介ヘルパー(CGNAT対応、4.2節)の実装**

- 新規 `src/DanoniEditor.Collab/Rendezvous/`
  - `RendezvousMessage.cs`: 待ち合わせ専用メッセージ(Hello/PeerAddress/Go/Failed)。共同編集本体の
    CollabMessageとは意図的に別系統(仲介役はセッションの中身を一切解釈しないため)。
  - `RendezvousHelperServer.cs`: 仲介ヘルパー役。同じSessionCode(Discord等で口頭合わせする合言葉)を
    持つ2接続をペアリングし、互いの外向きIP:ポート(TCP接続そのものがSTUNのエコー応答を兼ねるため
    専用のエコーパケットは不要)を通知したうえで同期合図を送り、役目を終える。相手が現れないまま
    既定3分(コンストラクタで変更可、テストでは短縮)経過すると失敗通知を返す。
  - `RendezvousClient.cs`: CGNAT側の実装。ヘルパーとの通信に使ったローカルポートを覚えておき、
    同期合図を受けたら同じポートから相手の外向きアドレスへ接続を試みる(TCP同時オープン)。
    **実装中に判明した知見(2026-09-20)**: 双方が同期合図を受け取ってから実際に接続を試みるまでの
    実時間には、ヘルパーまでの往復遅延の違い等でズレが生じるため、1回きりの同時接続試行では
    高確率で失敗する(ループバック環境での結合テストで実際にConnection refusedを確認)。これを受け、
    15秒の制限時間内、200ms間隔で接続を繰り返し試みる方式へ変更し、ループバック環境では安定して
    成立することを確認した。ただし、この挙動確認はあくまでループバック(同一マシン内、NAT無し)での
    ものであり、実際のCGNAT環境(NATの種類による差、双方の実際のインターネット経路のRTTなど)での
    成立可否は別途要検証。
- `src/DanoniEditor.Collab/Transport/RendezvousConnection.cs`: 待ち合わせメッセージ専用の送受信層
  (CollabConnectionと同じフレーミング形式だが、既存のCollabConnection・その利用側のテストに影響を
  与えないよう、あえて独立したクラスとして追加)。
- `CollabHost.AcceptExternalClient` / `CollabGuestClient.ConnectViaExistingSocketAsync` を追加。
  TCP同時オープンで確立済みの接続を、通常のAccept/Connect経由の接続と全く同じ扱いで
  ハンドシェイク〜以降のプロトコルへ合流させられるようにした(接続さえ確立すれば、それが
  仲介ヘルパー経由かどうかの区別はプロトコル上不要という設計のため)。
- テスト: `tests/DanoniEditor.Core.Tests/Collab/RendezvousTests.cs`(4件、全て成功)。
  ヘルパーによるペアリング・タイムアウト・(ループバック上での)TCP同時オープンの成立・
  確立した接続をCollabHost/CollabGuestClientへ引き渡せることまで、相手役の人間を必要とせず
  自動テストで検証済み。
- 実装環境の制約により、このセッションでは実機(.NET SDK)でのビルド確認ができていない。次回、ユーザー環境で`dotnet build`/`dotnet test`による確認が必要。

**2026-09-20 追記: ビルド・テスト検証結果**

Claude作業環境のクラウド側コンテナ(`device_bash`とは別系統)に.NET 8 SDK(8.0.425)を導入し、`DanoniEditor.Core`/`DanoniEditor.Editing`/`DanoniEditor.Collab`/`DanoniEditor.Core.Tests`一式をステージングして検証した。

- `dotnet build tests/DanoniEditor.Core.Tests/DanoniEditor.Core.Tests.csproj` → **ビルド成功(エラー0件)**
- `dotnet test` → 682件中674件成功・8件失敗。失敗した8件はいずれも`Models/RealTemplate*Tests.cs`・`Analysis/IttnAnalyzerTests.cs`内の、リポジトリ直下`./template`ディレクトリ(実テンプレートファイル群)を参照するテストであり、当該ディレクトリを検証環境にステージングしていなかったことによる`DirectoryNotFoundException`。**Collab関連の実装・修正とは無関係の環境起因の失敗**であり、ユーザーのローカル環境(フルリポジトリ)では発生しない見込み。
- `--filter "FullyQualifiedName~Collab"` で新規追加分のみ実行 → **14件全て成功**(`CellDiffApplierTests`・`CollabHandshakeTests`のループバックTCP結合テスト含む)

以上により、フェーズ1(MVP)の通信基盤・セル差分同期エンジンのコード品質・単体/結合テストの妥当性を確認できた。


**2026-09-20 追記: セル差分方式への移行(簡易版からの置き換え、5ステップ計画のStep1)**

ユーザー方針決定(「全体スナップショットを受信→差分を判定 という流れは無駄。セル毎の処理をきちんと
採用しないといけない」)を受け、上記「簡易版:双方向スナップショット送信」を廃止し、当初の設計メモ
2.2節どおりの真のセル差分同期へ置き換えた。`EditActions.cs`(79KB規模)側の個々の操作へ「自分が
どのセルを変更したか」を報告させる広範な改修は避け、代わりに`EditorDocument`側へ汎用の
`BeforeEdit`/`AfterEdit`イベントを追加し、編集ジェスチャ前後の`DifficultyTab`状態を外側
(Collab層)から比較する方式を採った。

- `src/DanoniEditor.Editing/EditorDocument.cs`: `BeforeEdit`/`AfterEdit`(いずれも`Action<DifficultyTab>?`)
  を追加し、`Execute`/`Undo`/`Redo`それぞれの処理本体の前後で発火するよう配線。`EditActions.cs`自体は
  無改修(「EditorDocumentはWPF/Collabから独立させる」既存の設計境界を維持)。
- 新規 `src/DanoniEditor.Collab/Sync/CellDiffDetector.cs`: `Capture(tab)`で編集前のレーン状態
  (ノートtick集合・フリーズ始点終点)を値としてスナップショットし、`Diff(tabIndex, before, tab)`で
  編集後の現在状態と比較、変化のあったセルぶんの`NoteCellChangedMessage`/`FreezeChangedMessage`を
  列挙する。ノートはHashSet差分、フリーズはStartTickをキーにしたDictionary差分(終点変更も
  「同一StartTickの置き換え」としてSet扱いで検出)。レーン数が採取後に変わっていた場合
  (テンプレート変更等の稀なケース)は共通範囲のみ比較し例外を投げない。
- `src/DanoniEditor.App/Collab/CollabSessionController.cs`: 全面書き換え。
  - `_document.Changed`の500msデバウンス送信を廃止し、`BeforeEdit`/`AfterEdit`に置き換え
    (`OnBeforeLocalEdit`でタブ状態をキャプチャ、`OnAfterLocalEdit`で`CellDiffDetector.Diff`を実行し
    生成された各メッセージを即座に送信)。
  - ホストは自分が受信したセル差分メッセージを適用後、送信元以外の全参加者へ即座に中継
    (`CollabHost.BroadcastAsync`の`excludeParticipantId`を利用、新設の`RelayAsync`ヘルパー経由)。
    「全体状態を常に配る」旧方式から「変化したセルだけを配る」方式に変わったことで、参加者数・
    タブサイズに対する通信量が大幅に削減される。
  - 初回参加時の状態同期(新規参加者がゼロから現在の全体像を受け取る必要がある場面)のみ、従来通り
    `SnapshotMessage`+`SnapshotSync.ApplySnapshotInPlace`を使用(これは差分では原理的に代替できない
    ため維持)。
  - `_applyingRemoteSnapshot`ガードフラグは不要になったため削除(送信元がそもそも
    `BeforeEdit`/`AfterEdit`経由のローカル編集ジェスチャからしか発火しないため、リモート適用による
    無限ループの心配が無い)。
  - 公開イベント`RemoteSnapshotApplied`を`RemoteEditApplied`へ改称(スナップショット適用に限らず、
    セル差分適用時にも発火するため)。`MainWindow.xaml.cs`側の購読箇所もあわせて更新。
- ビルド確認: `DanoniEditor.Core.Tests.csproj`・`DanoniEditor.App.csproj`
  (`-p:EnableWindowsTargeting=true`)いずれもエラー0件で成功。
- テスト: 新規`tests/DanoniEditor.Core.Tests/Collab/CellDiffDetectorTests.cs`(9件、ノート追加/削除/
  移動・フリーズ追加/終点変更/削除・複数レーン同時変更・レーン数減少時の範囲外無視・無変化時に
  メッセージ0件、を検証)を追加、全て成功。既存Collab/Rendezvousテスト18件も回帰無く全て成功
  (`RendezvousTests`の1件はクラウド検証環境特有のネットワークタイミングにより初回のみ失敗したが、
  再実行で安定して成功しており、既知のとおりループバック環境固有の揺らぎであって今回の変更による
  回帰ではない)。全体テスト(695件、うち8件はテンプレートディレクトリ未ステージングによる既知の
  環境起因失敗)も687件成功で新規リグレッション無し。

**次のステップ(5ステップ計画、Step2以降)**:
Step2 ノート所有者アイコン(セッション中のみのephemeralな所有者情報、`NoteCellChangedMessage`/
`FreezeChangedMessage`への送信者ID付与・ホスト自身の識別情報整備・`ChartCanvas.cs`での丸アイコン
描画)、Step3 配布ディレクトリ整理(DLLをEXE本体と同階層から`lib`等の一段下ディレクトリへ移動)、
Step4 AvalonDock導入+右パネルのドック化改修、Step5 参加者一覧パネル、の順で進める(ユーザー承認済み)。


**2026-09-20 追記: ノート所有者アイコン(設計メモ6.4節、5ステップ計画のStep2)**

ユーザー要望「共同タイムライン編集中にノートの左上に丸アイコンを表示、参加者が自らの色として
設定したものを適用すれば『そのノートを誰が置いたのか』が見えて良い」を実装。所有者情報は
ユーザー確定仕様どおりセッション中のみのephemeralな情報とし、プロジェクトファイル
(ChartProject/ProjectSerializer)へは一切保存しない。

- `src/DanoniEditor.Collab/Protocol/CollabMessage.cs`: `NoteCellChangedMessage`/`FreezeChangedMessage`
  へ`AuthorParticipantId`(既定値は空文字、既存呼び出し箇所への影響を避けるためoptional)を追加。
- `src/DanoniEditor.Collab/Sync/CellDiffApplier.cs`: 3つのメッセージ生成ファクトリ
  (`CreateNoteCellMessage`/`CreateFreezeSetMessage`/`CreateFreezeRemoveMessage`)へ
  `authorParticipantId`引数(既定値空文字)を追加。
- `src/DanoniEditor.Collab/Sync/CellDiffDetector.cs`: `Diff`メソッドへ`authorParticipantId`引数
  (必須)を追加し、生成する全メッセージへ付与するようにした。
- 新規 `src/DanoniEditor.Collab/Sync/NoteOwnershipTracker.cs`: 「どのセル(タブ番号・レーン番号・
  tick/StartTick)を誰が最後に変更したか」をメモリ上のみで追跡する軽量クラス。ChartProjectには
  一切触れない(Core/Collab層側の既存の「本体データとは疎結合」という設計を維持)。制約として、
  ①自分が参加する前に置かれていたノート/フリーズの所有者は分からない(このセッションで実際に
  変更イベントを観測したセルのみ追跡対象)、②離脱した参加者の識別色は空くと新規参加者へ
  再割当されうるため、古い所有者表示が新しい参加者と同じ色に見えることがある、という2点を
  ephemeral/簡易表示前提の許容される限界として受け入れている。
- `src/DanoniEditor.Collab/Session/CollabHost.cs`: `Self`プロパティ(`ParticipantInfo?`)と
  `SetSelfIdentity(displayName, preferredColor)`を追加。従来ホスト自身は「参加者」として
  ロスターに現れず(HelloMessage/WelcomeMessageの往復が無いため)、他の参加者がホストの表示名・
  識別色を知る手段が無かった(=ホスト自身が置いたノートの所有者色をゲスト側で解決できない、
  という設計上の穴があった)。`SetSelfIdentity`をStart()前に呼ぶことでホスト自身を内部ロスターへ
  登録し、以後は新規参加者へのWelcomeMessage.Rosterに自動的に含まれるようになる(新しいメッセージ種を
  増やさず、既存の参加フローへホストを1参加者として乗せるだけで解決した)。呼ばない限り従来通りの
  挙動のままなので、下位のCollabHandshakeTests.cs(host.Rosterやguest.InitialRosterを直接検証)は
  無改修のまま全て成功する。
- `src/DanoniEditor.App/Collab/CollabSessionController.cs`: `Self`(自分自身の`ParticipantInfo`)を
  公開。`StartHost`で`host.SetSelfIdentity(displayName)`を呼ぶようになり、従来使われていなかった
  `displayName`引数が初めて実際に使われるようになった(地味な既存不具合の解消でもある)。
  `OnAfterLocalEdit`は`CellDiffDetector.Diff`へ`Self.Id`を渡し、生成した各メッセージを送信と同時に
  `NoteOwnershipTracker`へも反映する(自分が置いたノートにも自分の色のアイコンが即座に表示されるよう
  にするため)。`OnHostMessageReceived`は、受信したメッセージの`AuthorParticipantId`を(クライアントの
  自己申告のままではなく)実際のTCP接続の参加者ID(`participantId`引数)で必ず上書きしてから適用・
  中継する(recordの`with`式で必要な場合のみ複製。なりすまし対策と、クライアント側の実装ミスがあっても
  所有者表示だけは必ず正しくなることの両方を狙った)。`GetNoteOwnerColor`/`GetFreezeOwnerColor`を
  新設し、ChartCanvas側からの色問い合わせに応じる。切断時は`NoteOwnershipTracker.Clear()`と
  `Self = null`を行う(セッション単位でリセット)。
- `src/DanoniEditor.App/ChartCanvas.cs`: `CollabSession`プロパティ(`CollabSessionController?`、
  `Document`/`Controller`と同じ単純なCLRプロパティとしての受け渡し)を追加。`DrawWarningOverlay`/
  `DrawImmediateApplyOverlay`/`DrawCommentIconOverlay`と同じ「ノート位置へ小さなアイコンを重ね描き
  する」既存パターンに倣い、`DrawCollabOwnerOverlay`(ノート/フリーズ始点の左上コーナーへ、白縁取り
  付きの小さな塗り丸を描く)を新設し、`DrawNotesAndFreezes`内の通常ノート・フリーズそれぞれの
  描画ループへ組み込んだ。`CollabSession`が未設定(共同編集セッション無し)の間は何も描かれず、
  既存の見た目に一切影響しない。
- `src/DanoniEditor.App/MainWindow.xaml.cs`: `AttachCollabUiHandlers`でセッション開始時に
  `Canvas.CollabSession`(分割ビューON時は`Canvas2.CollabSession`も)を設定し、`Disconnected`時に
  クリアして再描画する(切断直後に所有者アイコンが表示され続ける不具合を防ぐ)。`SyncCanvas2FromCanvas`
  にも`CollabSession`の追従コピーを追加(セッション開始後に分割ビューをONにした場合にも反映されるように)。
- ビルド確認: `DanoniEditor.Core.Tests.csproj`・`DanoniEditor.App.csproj`
  (`-p:EnableWindowsTargeting=true`)いずれもエラー0件で成功。
- テスト: 新規`tests/DanoniEditor.Core.Tests/Collab/NoteOwnershipTrackerTests.cs`(8件、追跡なし時の
  null・追加/削除/上書きでの所有者記録・フリーズの追加/削除・別セルへの非干渉・Clearでの全消去、を
  検証)を追加、全て成功。既存`CellDiffDetectorTests.cs`(9件)は`Diff`呼び出しへ`AuthorParticipantId`
  引数を追加する形で更新し、うち2件は生成メッセージの`AuthorParticipantId`が正しく伝播していることも
  検証するようにした。既存のCollab/Rendezvous/CellDiffApplierテスト(26件)も回帰無く全て成功。
  Collab関連テストは計35件全て成功。全体テストも703件中695件成功で新規リグレッション無し
  (残る8件は前回・前々回同様、テンプレートディレクトリ未ステージングによる環境起因の既知失敗のみ)。

**次のステップ(5ステップ計画、Step3以降)**:
Step3 配布ディレクトリ整理(DLLをEXE本体と同階層から`lib`等の一段下ディレクトリへ移動)、
Step4 AvalonDock導入+右パネルのドック化改修、Step5 参加者一覧パネル(今回追加した
`CollabSessionController.Self`・既存の`RosterChanged`イベントをそのまま使って表示名・識別色の
一覧を組める見込み)、の順で進める(ユーザー承認済み)。


**2026-09-20 追記: 配布ディレクトリ整理(5ステップ計画のStep3、DLLを"lib"サブフォルダへ)**

ユーザー要望「現在EXE本体と各種DLLを同じディレクトリに入れているので、一段下の"lib"ディレクトリへ
移してEXE本体ディレクトリをスッキリさせたい」を実装。共同編集そのものとは直接関係しないが、
Step4のAvalonDock採用検討(「単一EXE配布とは言っても結局各種DLLは既に同梱している」という
ユーザーの前提認識)とセットで出てきた要望のため、この5ステップ計画のStep3として扱う。

前提となる調査結果(`docs/progress_and_tbd_2026-07-22.md`4章、2026-07-22時点で既に判明していた内容):
単独exe配布(`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`)であっても、
WPF特有の少数のネイティブ相互運用DLL5個(`D3DCompiler_47_cor3.dll`・`PenImc_cor3.dll`・
`PresentationNative_cor3.dll`・`vcruntime140_cor3.dll`・`wpfgfx_cor3.dll`、いずれも
`*_cor3.dll`という共通の命名規則を持つ)は.NET SDK側の既知の制約により単一exeへ埋め込めず、
必ずexeと同じ階層に別ファイルとして出力される(このエディタ固有の問題ではない)。ユーザーが
「各種DLL」と呼んでいたのはこの5個のこと。

実装方針: これらのDLLは実行時にWPF側の管理コードからLoadLibrary相当のPInvoke経由で遅延ロードされる
(OSがexe自体を起動する時点ではまだ読み込まれない)ため、「exeと同じ階層のlibフォルダ」を
Win32のネイティブDLL検索パスへ最優先で追加しておけば、実体をlibフォルダへ移動しても解決できる。

- 新規 `src/DanoniEditor.App/Program.cs`: 明示的なエントリポイント(`[STAThread] static void Main()`)。
  `AddDllDirectory`/`SetDefaultDllDirectories`(Win32 API、PInvoke)で、exeと同じ階層の`lib`フォルダを
  ネイティブDLL検索パスへ追加してから`new App().InitializeComponent(); app.Run();`する。
  保険としてPATH環境変数の先頭にも同じパスを足す。`lib`フォルダが存在しない場合(開発時の
  `dotnet build`/`dotnet run`実行時など、単独exe配布以外の実行形態)は何もしない。
- `src/DanoniEditor.App/DanoniEditor.App.csproj`:
  - `App.xaml`は既定では(ファイル名が`App.xaml`であるため)暗黙的に`ApplicationDefinition`扱いとなり、
    WPFのビルド処理がMainを自動生成してしまう(=Program.Mainへ処理を挟めない)ため、
    `<ApplicationDefinition Remove="App.xaml" /><Page Include="App.xaml" />`で明示的に`Page`扱いへ変更し、
    `<StartupObject>DanoniEditor.App.Program</StartupObject>`でエントリポイントを差し替えた
    (WPFアプリで「Mainの先頭に処理を挟みたい」場合の標準的な回避策)。
  - `MoveNativeInteropDllsToLib`ターゲット(`AfterTargets="Publish"`)を追加。`$(PublishDir)*_cor3.dll`に
    マッチするファイルをpublish完了後に`lib`サブフォルダへ物理的に移動する。ファイル名固定ではなく
    パターンマッチにしているため、将来のSDKバージョンアップ等で対象DLLの構成が微妙に変わっても
    (同じ命名規則が保たれる限り)追従できる。
  - `template`/`img`/`sounds`フォルダはDLLではなくユーザー向けデータのため、今回の整理対象からは
    意図的に除外している(従来通りexe本体と同階層のまま)。
- ビルド確認: `dotnet build`(Debug、`-p:EnableWindowsTargeting=true`)はエラー0件で成功。
  加えて、クラウド側検証環境で実際に`dotnet publish -c Release -r win-x64 --self-contained true
  -p:PublishSingleFile=true`まで実行し(Linux上でもWindows向けのpublish自体は実行可能、
  実行ファイルの起動さえしなければクロスプラットフォームで検証できる)、出力を直接確認した:
  publishフォルダ直下には`DanoniEditor.App.exe`(+ pdbファイル群)のみが残り、5個のネイティブDLLは
  すべて期待通り`lib`サブフォルダへ移動されていることを確認した。
- **未検証(実機での確認が必要)**: 今回確認できたのはあくまで「publish後のファイル配置」までで、
  実際にWindows上で`DanoniEditor.App.exe`を起動し、`Program.cs`の`AddDllDirectory`呼び出しが
  意図通り機能して(WPFのネイティブレンダリング等が`lib`フォルダのDLLを正しく見つけて)
  アプリが正常に起動・動作するかどうかは、クラウド側の検証環境(Linux)では確認できない
  (WPFアプリはWindows上でしか実行できないため)。`tools\bump_and_publish.ps1`で一度publishし、
  出力フォルダの`DanoniEditor.App.exe`をダブルクリックして通常通り起動・操作できることを
  確認していただく必要がある。万一起動しない場合は、`Program.cs`の`AddDllDirectory`呼び出しが
  WPFネイティブDLLの実際のロードタイミングより後になっている可能性が高い
  (その場合は`SetDllDirectory`との併用や、環境変数`PATH`設定のみへの単純化などの代替案を検討する)。

**次のステップ(5ステップ計画、Step4以降)**:
Step4 AvalonDock導入+右パネルのドック化改修、Step5 参加者一覧パネル、の順で進める(ユーザー承認済み)。


**2026-09-21 追記: 配布ディレクトリ整理、実機検証での起動不具合修正**

ユーザーに`tools\bump_and_publish.ps1`で実際にpublish・起動していただいたところ、
「ダブルクリックするとカーソルが砂時計になるが、その後何も起きない(ウィンドウが出ない)」
という不具合が発生した。

原因(推定): 前回実装は Win32 の`AddDllDirectory`(プロセス全体のネイティブDLL検索パスへの追加)
のみで対応していたが、`PresentationNative_cor3.dll`等はWPFフレームワーク側の内部コード
(PresentationCore.dll/PresentationFramework.dll)が`[DllImport]`で参照しており、その呼び出し
自体はこちらのコードで制御できない。.NET自身のP/Invokeネイティブライブラリ解決が
`AddDllDirectory`で追加した検索パスを実際に辿ってくれる保証は無く(既定の解決経路で
完結してしまい、Win32レベルの検索パス追加までフォールバックしない可能性がある)、
起動処理の途中(まだメッセージボックスも出せない段階、DispatcherUnhandledExceptionの
購読より前)でネイティブDLLが見つからずに静かに落ちていたと考えられる。

修正: `src/DanoniEditor.App/Program.cs`に、.NET自身が「既定の解決に失敗した場合にのみ」
呼んでくれる専用フック`AssemblyLoadContext.Default.ResolvingUnmanagedDll`を主対策として追加した。
これは「どのDllImport宣言か」に関わらず既定のネイティブライブラリ解決が失敗した際に必ず呼ばれるため、
WPFフレームワーク内部のDllImportに対しても確実に介入できる(こちらが所有していないコードの
DllImport呼び出しにフックできる、.NET側が用意した正規の拡張点)。従来の`AddDllDirectory`は、
DLL同士のネイティブな相互依存(例:  いずれかのDLLが同じlibフォルダの別DLLを自分の依存先として
必要とする、というOSローダー自身が解決するケース)への保険として残してある。

- クラウド側検証環境で`dotnet build`(Debug)・`dotnet publish`(Release、win-x64、self-contained、
  single-file)いずれも再確認し、エラー0件・publish後の配置(`lib`フォルダへの5DLL移動)も
  従来通り正しいことを確認済み。
- **引き続き実機での起動確認が必要**: 上記はあくまで推定原因に基づく修正であり、
  クラウド側検証環境(Linux)では「実際にWindows上で起動するか」は検証できないため、
  `tools\bump_and_publish.ps1`で再度publichしていただき、起動確認をお願いしたい。
  万一これでも起動しない場合は、根本原因が別にある可能性が高いため、いったんDLL配置の変更自体を
  ロールバック(exeと同階層に戻す)し、原因の切り分け(Program.cs/StartupObject変更自体の影響か、
  ネイティブDLL解決の話とは別の要因か)を先に行う方針に切り替える。

**実機確認結果(2026-09-21)**: ユーザーに`tools\bump_and_publish.ps1`で再publish・起動していただき、正常に起動することを確認済み。`ResolvingUnmanagedDll`フックによる修正で解決した。これでStep3(配布ディレクトリ整理)は実機確認まで含めて完了。

## 2026-09-21 追記: AvalonDock導入・右パネルのドック化改修 第1段階(5ステップ計画のStep4)

### 採用ライブラリの選定
- `Xceed.Wpf.AvalonDock`(最新5.2.x)は現在商用ライセンス化されており(45日間トライアル後に有償ライセンスが必要)、
  採用不可と判断。
- 代わりにMITライセンスのコミュニティフォーク`Dirkster.AvalonDock`(https://github.com/Dirkster99/AvalonDock)を採用。
  バージョンは**4.74.1**固定。理由: 4.74.1以前は`net5.0-windows7.0`を対象としており本プロジェクトの
  `net8.0-windows`から(NuGetのTFMフォールバックにより)解決できるが、5.0.0以降は`net9.0-windows7.0`/
  `net10.0-windows7.0`/`.NETFramework4.8`のみが対象で、`net8.0-windows`からは解決できないため。
- テーマ用に`Dirkster.AvalonDock.Themes.VS2013`(同じく4.74.1)も追加。VS2013 Lightテーマを適用。
- XAML名前空間URI(`xmlns:xcad="https://github.com/Dirkster99/AvalonDock"`)は、NuGetパッケージを実際に
  ダウンロード・展開し、`System.Reflection.MetadataLoadContext`でアセンブリのメタデータを読み込んで
  `XmlnsDefinitionAttribute`を直接列挙する方式で確認した(文字列検索(strings)だけでは、埋め込まれている
  文字列がどの属性の引数として使われているか断定できなかったため)。AvalonDock本体・AvalonDock.Themes.VS2013
  いずれも同一のURIへ`AvalonDock`/`AvalonDock.Controls`/`AvalonDock.Converters`/`AvalonDock.Layout`/
  `AvalonDock.Themes`の5つのclr-namespaceがマッピングされていることを確認済み(単一のxmlnsエイリアスで
  全て参照可能)。

### 実施内容(第1段階: 既存10タブ+プラグイン動的追加+タブ自動切替ロジックを、見た目・挙動をほぼそのまま
  1つのペインにタブとして並ぶだけの状態でドッキング化)
- `DanoniEditor.App.csproj`: `Dirkster.AvalonDock`・`Dirkster.AvalonDock.Themes.VS2013`(いずれも4.74.1)への
  `PackageReference`を追加。
- `MainWindow.xaml`: 右パネルの`TabControl`(10個の`TabItem`、うち6個は既存のx:Name付き、4個は無名だった)を、
  `DockingManager`→`LayoutRoot`→`LayoutPanel Orientation="Vertical"`→`LayoutAnchorablePane`(x:Name=
  `PropertyAnchorablePane`)→`LayoutAnchorable`×10という構造へ置き換え。各`LayoutAnchorable`には
  `CanClose="False"`を付与(従来のTabItemに閉じるボタンが無かった挙動を維持)。内部のタブ内容(XAML)は
  一切変更していない。無名だった4タブには新たにx:Nameを付与: プロジェクト→`ProjectPropertyPane`、
  色設定→`ColorSettingsPane`、オブジェクト→`ObjectPropertyPane`、その他→`OtherPropertyPane`。既存の
  `ColorEditTabItem`/`MacroTabItem`/`MarkerTabItem`/`LinkTabItem`/`AnalysisTabItem`/`PreviewTabItem`の
  x:Nameはそのまま流用。`DockingManager`にはVS2013 Lightテーマを適用。
- `MainWindow.xaml.cs`:
  - `ObjectTabIndex`/`ProjectTabIndex`定数を削除し、インデックスベースの`PropertyTabControl.SelectedIndex`
    切替を、名前ベースの`ObjectPropertyPane.IsActive = true`/`ProjectPropertyPane.IsActive = true`へ置換
    (`AutoSwitchToObjectTab`・`RefreshSelectedObjectPanel`内の2箇所)。
  - `ToggleColorEditMode`: `PropertyTabControl.SelectedItem = ColorEditTabItem` → `ColorEditTabItem.IsActive = true`。
  - `RefreshAnalysisPanel`: `AnalysisTabItem.Visibility = ...`(`LayoutAnchorable`はFrameworkElementでは
    ないためVisibility属性を持たない)→ `.Show()`/`.Hide()`呼び出しへ変更。既定で非表示(未解禁)という
    従来のXAML側`Visibility="Collapsed"`と同じ初期状態を保つため、コンストラクタの`InitializeComponent()`
    直後に明示的に`AnalysisTabItem.Hide()`を呼ぶよう追加。
  - プラグインパネルの動的追加(`_pluginManager.PanelPlugins`ループ内): `PropertyTabControl.Items.Add(new
    TabItem {...})` → `PropertyAnchorablePane.Children.Add(new LayoutAnchorable { Title = ..., Content = ...,
    CanClose = false })`(`using AvalonDock.Layout;`を追加)。
  - `PropertyTabControl_PreviewMouseLeftButtonDown`(2026-08-01要望対応、マウスクリックでのタブ切替限定の
    フォーカス復帰処理、`FindTabItemAncestor`利用)を削除し、`DockingManager.ActiveContentChanged`イベント
    ハンドラ`PropertyDockingManager_ActiveContentChanged`へ置き換えた。**意図的な挙動変更**: 従来は
    マウスクリックでタブヘッダーを切り替えた場合のみフォーカス復帰処理が働き、キーボード操作やコード側の
    `SelectedIndex`変更は対象外だったが、`LayoutAnchorable`はFrameworkElementではなくマウスイベントを
    持たないため、原因を問わず(マウス・キーボード・コードいずれでも)常にCanvasへフォーカスを戻す方式へ
    単純化した。Tabキーでのフィールド間移動など、キーボード操作時の挙動に支障が出ないかは実機確認が必要。
  - 右パネルとは無関係な`FindTabItemAncestor`ヘルパー(`ProjectTabControl`/`DifficultyTabControl`の
    D&D・右クリックメニューで使用)、および`AdjustTabHeaderWidths`(同じく上段タブ用)は一切変更していない。

### 検証結果
- クラウドコンテナ内でのビルド(Debug、`-p:EnableWindowsTargeting=true`)が警告・エラー0件で成功。
- `dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true`でのpublishレイアウトも確認。
  Step3で導入した「WPFネイティブ相互運用DLL5個を`lib`サブフォルダへ移動する」処理が、AvalonDock関連の
  アセンブリ追加後も問題なく機能し、単一exe本体には正しく埋め込まれることを確認した(exeディレクトリ直下に
  想定外のDLLが残っていない)。
- テストスイート(`dotnet test`)は695/703件成功で、変更前と同じ既知の8件(テンプレートディレクトリが
  見つからない、環境依存の既存失敗、今回の変更とは無関係)のみ。今回はCore/Collab層のロジックには一切
  触れていない(App層のみの変更)ため、想定通り。

### 次のステップ
実機での起動・右パネルの挙動確認(タブ切替、プラグインパネル追加、フォーカス/ショートカットキー挙動、
分析タブの表示/非表示切替)をお願いしたい。確認が取れ次第、次段階(既定レイアウトを2〜3ペイン縦積みへ
変更)、そしてStep5(参加者一覧パネル)へ進む。

### 実機確認結果(2026-09-21)・既知の課題(TBD)

実機でビルド・起動し、右パネルの挙動(タブ切替、プラグインパネル、ショートカットキー、分析タブの表示/非表示)を
確認いただき、問題なく動作することを確認済み。

その上で新たなTBDが1件見つかった: **右パネルのタブヘッダーが、パネル幅が狭いとタブ数に対して横幅不足になり
表示が潰れる**(従来のTabControlでは`AdjustTabHeaderWidths`的な明示対応は無かったが、タブ数が少なかった/
標準TabControlの折り返し挙動だったため目立たなかった可能性がある。AvalonDockの`LayoutAnchorablePane`は
既定では折り返さずタブ幅を圧縮する挙動のため、10タブ+プラグイン動的追加分が並ぶと顕在化したと考えられる)。
対策案(未着手、次回以降で検討): ①`RightPanelColumn`の既定幅を広げる、②タブヘッダーに最小幅を設けて
横スクロール可能にする、③複数ペイン化(次段階で予定)により1ペインあたりのタブ数を減らすことで自然に緩和する
可能性がある(③は既定レイアウト変更のステップと合わせて効果を見てから追加対策の要否を判断するのが効率的)。

## 2026-09-21 追記: AvalonDock導入・右パネルのドック化改修 第2段階(既定レイアウトの複数ペイン化)

第1段階(全10タブ+プラグインパネルが1つの`LayoutAnchorablePane`にまとまっているだけの状態)から、
既定で複数ペインが縦に積まれた状態へ変更した。`MainWindow.xaml.cs`側の呼び出し箇所(各`LayoutAnchorable`
のx:Nameを使った`IsActive`切替等)はペインの所属を変えても壊れないため、コード側は無変更。

### ペイン構成(`LayoutPanel Orientation="Vertical"`配下、上から順に)
1. `PropertyAnchorablePane`(既存名を流用): プロジェクト・色設定・オブジェクト・その他 + プラグイン
   パネル(動的追加分もここへ入る、コード側の追加先は変更なし)。
2. `EditToolsAnchorablePane`(新設): 色編集・マクロ・マーカー・リンク。
3. `AnalysisAnchorablePane`(新設): 分析・プレビュー。

グルーピングの意図: ①プロジェクト全体設定・選択オブジェクトのプロパティ(頻繁に参照)、②編集ツール系
(モード切替・マクロ実行など操作寄り)、③分析・プレビュー(結果確認寄り)、という性質の違いで分けた。
各ペインの高さ配分はAvalonDockの既定(均等割り、ペイン間はドラッグでリサイズ可能)に任せている。

### 検証結果
- クラウドコンテナ内でのビルド(Debug、`-p:EnableWindowsTargeting=true`)・publishレイアウト確認(Release、
  win-x64単独exe、`lib`フォルダへのネイティブDLL退避含む)とも問題なし。

### 副次効果(2026-09-21実機確認で見つかったタブ横幅不足への対策案③)
1ペインあたりのタブ数が10→最大4まで減ったため、タブヘッダーの横幅不足による表示崩れがどの程度緩和されるかは
実機確認待ち。緩和が不十分であれば、上述の対策案①②(パネル既定幅の拡大、タブヘッダーの最小幅+横スクロール)を
追加で検討する。

### 次のステップ
実機での確認(複数ペインが意図通り縦に並ぶか、ペイン間のリサイズ・タブ切替が問題なく動くか、タブ横幅問題が
どの程度緩和されたか)をお願いしたい。確認が取れ次第、Step5(参加者一覧パネル)へ進む。

## 2026-09-21 追記: 右パネルのタブ位置を下側→上側へ変更(ユーザー要望)

AvalonDockの既定では、ツールウィンドウ系ペイン(`LayoutAnchorablePane`)のタブはVisual Studioのツール
ウィンドウと同じ「下側」に表示される。調査の結果、`LayoutAnchorablePaneControl`には`TabStripPlacement`
というプロパティ自体は存在するものの、テーマ(VS2013テーマ・AvalonDock標準テーマとも)のControlTemplateが
タブ行を`Grid.Row="1"`固定で配置しており、`TabStripPlacement`の値を変えるだけでは表示位置が変わらないことを
確認した(AvalonDock本体・VS2013テーマ両方のソースをGitHub上で参照して確認)。

対応として、`DockingManager`が持つ`AnchorablePaneControlStyle`プロパティ(テーマが暗黙スタイル経由で
設定している、正式なプロパティ)へ、タブ行を`Grid.Row="0"`(上側)・コンテンツ領域を`Grid.Row="1"`側へ
入れ替えた独自の`ControlTemplate`を明示的に指定する形で上書きした。テーマ固有の`DynamicResource`キー名
(配色定義)には一切依存させず、`TemplateBinding`のみで組む単純な見た目にしている(アプリの他の素の
`TabControl`(上段の難易度タブ・プロジェクトタブなど)に近い見た目になる)。`MainWindow.xaml`の
`PropertyDockingManager`要素に`Resources`としてスタイルを追加し、`AnchorablePaneControlStyle`属性で
明示的に参照する形。ペイン構成・タブの中身・`MainWindow.xaml.cs`側のコードは一切変更していない
(見た目のみの変更)。

### 検証結果
クラウドコンテナ内でのビルド(Debug)・publishレイアウト確認(Release、単独exe化)とも問題なし。実機での
見た目確認は未実施(タブが実際に上側へ表示されるか、独自テンプレートの見た目に違和感がないか)。

### 2026-09-21 追記: タブ上側化での起動エラー修正

実機での起動時、「起動に失敗しました: 'System.Windows.StaticResourceExtension' の値の指定時に例外が
スローされました。」という起動エラーダイアログが2回中2回発生することが報告された。

**原因**: WPFのXAMLパーサは、ある要素自身の属性(この場合`DockingManager`の`AnchorablePaneControlStyle`属性)
を、その要素の子コンテンツ(プロパティ要素構文で書いた`DockingManager.Resources`)が組み立てられるより先に
評価する。そのため「同じ要素自身のResourcesに定義したStyleを、同じ要素の属性からStaticResourceで参照する」
という書き方は、参照時点でまだそのResourcesが存在せず解決に失敗する(WPFのよく知られた既知の落とし穴)。
今回`AnchorablePaneTopTabsStyle`を`DockingManager.Resources`内に置き、同じ`DockingManager`要素の
`AnchorablePaneControlStyle`属性から参照していたため、これに該当していた。

**修正**: スタイル定義を`DockingManager.Resources`から、より外側(祖先)の`Window.Resources`へ移動した。
祖先要素のResourcesは、その子孫要素(DockingManagerとその属性)が組み立てられるより確実に先に用意されるため、
このタイミング問題を回避できる。`AnchorablePaneControlStyle`属性側の記述(`{StaticResource
AnchorablePaneTopTabsStyle}`)・スタイルの中身自体は変更していない。

クラウド側でビルド・publishレイアウト確認は再度問題なし。実機での再検証をお願いしたい。

### 2026-09-21 追記: タブ内容がToString()表示になる不具合の修正(意図しない不具合)

実機確認で、3ペインへの分割自体は正しく表示されたものの、各タブの見出し・中身が実際のプロパティパネルではなく
`AvalonDock.Layout.LayoutAnchorable`というテキスト(モデルオブジェクトの既定ToString()表示)になってしまう
不具合が報告された。**これは意図したものではなく、タブ上側化のために作った独自`Style`の不備によるバグ**。

**原因**: `AnchorablePaneControlStyle`をテーマ既定のものから丸ごと自作の`Style`に差し替えた際、`Template`
(ControlTemplate、タブ行とコンテンツ領域の配置を上下入替えるためのもの)しか設定しておらず、テーマ側が本来
設定している`ItemTemplate`(タブ見出しの描画方法)・`ContentTemplate`(選択中タブの中身の描画方法)を
引き継いでいなかった。これらが未設定だとWPFは既定の「オブジェクトのToString()をそのまま表示する」挙動に
フォールバックするため、あの表示になっていた。

**修正**: 自作`Style`に`ItemTemplate`(`<xcad:LayoutAnchorableTabItem Model="{Binding}"/>`)と
`ContentTemplate`(`<xcad:LayoutAnchorableControl Model="{Binding}"/>`)を明示的に追加した(いずれも
AvalonDockが標準で使うテンプレートで、テーマ既定のAnchorablePaneControlStyleが元々設定している値と同じ)。
タブ位置(上側)自体の実装方針は変更していない。

クラウド側でビルド・publishレイアウト確認は再度問題なし。実機での再検証をお願いしたい。
