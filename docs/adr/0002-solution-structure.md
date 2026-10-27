# ADR-0002: ソリューション構成とドメインロジックの置き場所

- ステータス: 承認（2026-10-28）
- 日付: 2026-10-23

## 結論

ドメインモデルを独立したプロジェクト `RoomBooking.Domain` に置き、ユースケースは API プロジェクトの中で機能ごとのスライス（Vertical Slice）に分ける。ドメインのルールは、DB を知らない **純粋関数** として書き、必要なデータは呼び出し側（スライスのハンドラー）が読み込んで引数で渡す（Functional Core, Imperative Shell）。

## 背景

- 機能は 3〜5 個（会議室一覧、予約の作成・取り消し、将来は時間変更）と小さい。
- 開発の多くを AI と一緒に進める。AI に任せやすいのは、1 つの変更が狭い範囲に収まり（**局所性**）、既存のコードを真似すれば済む（**真似しやすさ**）構成である。
- 一方で、AI は「動けばいい」近道をしやすい。ドメインが EF Core に依存する、ハンドラーに業務ルールを書くといった違反は、機械的に止められる（**境界の強制**）ほうがよい。
- `TimeSlot`、`Reservation`、重複チェックは、予約の作成・時間変更・一覧など複数の機能から共有される。
- 計画の柱に、DDD と関数型スタイル（Result 型）がある。

## 検討した選択肢

### プロジェクトの分け方

| | 層で分ける（Domain / Application / Infrastructure / Api） | 純粋な Vertical Slice（1 プロジェクト） | ハイブリッド（Domain プロジェクト ＋ API 内のスライス） |
|---|---|---|---|
| 局所性 | 弱い。1 機能を足すのに 4 プロジェクトを行き来する | 強い | 強い（ユースケースはスライスに収まる） |
| 境界の強制 | 強い。プロジェクト参照で依存の向きを縛れる | 弱い。同じプロジェクト内では近道を止められない | ドメインについては強い。Domain が外部に依存したらコンパイルエラーになる |
| ドメインモデルの共有 | できる | 業務ルールがハンドラーに散らばりやすい | できる |
| この規模でのコスト | プロジェクト数が多すぎる | 最小 | 小さい |

### ドメインのルールが既存データを必要とするとき（重複チェック）

| | A: Repository のインターフェースを Domain に置く（依存性逆転） | B: 必要なデータを引数で渡す（純粋関数） |
|---|---|---|
| 形 | `ReservationService(IReservationRepository)` が非同期で予約を探して判定する | `ReservationPolicy.Reserve(overlapping, room, slot, reserver)` が渡された一覧だけで判定する |
| テスト | Repository のスタブが必要 | 不要。リストを渡すだけ |
| 変更の波及 | インターフェースを変えると、実装クラス・DI の登録・テストのスタブまで直す | 関数と呼び出し元を直せば終わる |
| 分割・統合 | I/O と判定が混ざっているので分けにくい | 入力と出力だけで完結するので、分けるのもまとめるのも容易 |
| 弱点 | ドメインに非同期処理と I/O が入り込む | 呼び出し側が正しいデータを渡す責任を持つ |

## 決定

**ハイブリッド構成と B（純粋関数）を採用する。**

```
src/
  RoomBooking.Domain/            値オブジェクト、エンティティ、集約、ドメインの純粋関数。外部パッケージに依存しない
  RoomBooking.Api/
    Features/
      Rooms/
        ListRooms/               エンドポイント、リクエスト/レスポンス、ハンドラーを 1 か所に置く
      Reservations/
        CreateReservation/
        CancelReservation/
    Infrastructure/              EF Core の DbContext、マッピング、DB 例外の変換
  RoomBooking.AppHost/           Aspire
  RoomBooking.ServiceDefaults/   Aspire（OpenTelemetry、ヘルスチェック等）
tests/
  RoomBooking.Domain.Tests/      ドメインのユニットテスト（モック不要）
  RoomBooking.Api.Tests/         DB を含む結合テスト
```

依存のルール:

| プロジェクト | 参照してよいもの |
|---|---|
| `RoomBooking.Domain` | なし（.NET の標準ライブラリだけ） |
| `RoomBooking.Api` | `RoomBooking.Domain`、`RoomBooking.ServiceDefaults`、EF Core など |
| スライス同士 | 互いに参照しない。共有したいものは Domain に置く |

置き場所の判断基準:

- **業務ルール**（不正な値を作れない、時間帯が重ならない、本人しか取り消せない）→ Domain
- **処理の手順**（読み込む、Domain の関数を呼ぶ、保存する、エラーを HTTP の応答に変換する）→ スライスのハンドラー
- **技術的な詳細**（SQL、EF Core のマッピング、SQLSTATE の変換）→ Infrastructure

重複チェックの流れ:

1. ハンドラーが、同じ会議室で時間帯が重なる予約を DB から読み込む。
2. `ReservationPolicy.Reserve(overlapping, room, slot, reserver)` を呼ぶ。重なりがあれば `ReservationConflict`、なければ新しい `Reservation` を `Result` で返す。
3. ハンドラーが保存する。その間に割り込まれた場合は、DB の排他制約が止める（ADR-0001）。

`Reservation` のコンストラクタ（またはファクトリ）は `internal` にして、Domain の外からは `ReservationPolicy` を通さないと作れないようにする。

## 結果

良くなること:

- ユースケースの変更は、ほぼ 1 つのスライスのフォルダで完結する。AI に「既存のスライスと同じ形で」と頼める。
- ドメインが外部に依存しないことを、コンパイラが保証する。
- ドメインのテストは、入力を渡して出力を確かめるだけで書ける。

受け入れる代償:

- Api プロジェクトの中では、スライスが Infrastructure を直接使えてしまう。ハンドラーに業務ルールを書かない、という規律はコードレビューで守る。
- ハンドラーが正しいデータ（重なる予約の一覧）を渡す責任を持つ。渡し間違えても、DB の排他制約が最後に止める（ADR-0001）。
- 機能が大きく増えたら、Application 層や Infrastructure を別プロジェクトに切り出す判断が必要になる。

フォローアップ:

- [x] フェーズ 0 で、この構成のプロジェクトとプロジェクト参照を作る。
- [ ] スライスの「型」（ファイル名、エンドポイントの登録方法、ハンドラーの形）を最初の 1 本で決め、以降はそれを真似する。
- [ ] 必要になったら、Architecture Test（NetArchTest など）で「Api の Features が業務ルールを持たない」などの規約をテストにする。
