# コードリーディング Step 1: プロジェクトの構成と依存関係

2026-10-28 / 全ファイル読解の Step 1

## 結論

`.slnx` はプロジェクトの一覧、依存関係は各 `.csproj` に書かれている。`ProjectReference` を図にすると、ソリューションの形が分かる。

```
Api.Tests ──► AppHost ──► Api ──► Domain ◄── Domain.Tests
                           │
                           └────► ServiceDefaults
```

- Domain から出ていく矢印はない（依存ゼロ）。
- AppHost → Api の矢印は、ほかの矢印と意味が違う（下の「AppHost → Api」）。
- Api.Tests は Api ではなく AppHost を参照している（下の「Api.Tests → AppHost」）。

| ファイル | 決めていること |
|---|---|
| `RoomBooking.slnx` | どのプロジェクトがあるか、`/src/` と `/tests/` のフォルダ分け。依存関係は書かない |
| `*.csproj`（6 つ） | SDK、TFM、依存先 |

## 依存の書き方は 3 種類

| 書き方 | 依存先 | 例 |
|---|---|---|
| `ProjectReference` | 同じソリューションの別プロジェクト | Api → Domain |
| `PackageReference` | NuGet パッケージ（バージョンは CPM で管理。Step 0） | Api → `Aspire.Npgsql` |
| `FrameworkReference` | .NET 本体の大きな部品 | ServiceDefaults → `Microsoft.AspNetCore.App` |

ServiceDefaults は `Microsoft.NET.Sdk`（Web ではない）なので、ASP.NET Core の型を使うために `FrameworkReference` を明示している。

## Domain は依存ゼロ

守っているのは **依存の向き**（Dependency Rule）。テストしやすさはその結果。

```
変わりやすい（詳細）                 変わりにくい（方針）
Npgsql・PostgreSQL・HTTP  ──依存──►  「同じ部屋・同じ時間に予約は 1 つ」
```

- DB を替えても予約のルールは変わらない。逆向きの依存があると、変わりやすい側の都合で方針のコードを直すことになる。
- 参照は追加できてしまうので、検出の手段が要る。

| 方法 | 検出できること | コスト |
|---|---|---|
| コードレビュー（`.csproj` の差分を見る） | 依存の追加。見落としはありうる | 0 |
| アーキテクチャテスト（NetArchTest など） | 「Domain が Npgsql 名前空間を使っていない」をテストで落とせる | パッケージ 1 つ、テスト数本 |

- 判断: 今はレビューで十分。入れるならフェーズ 1 で Domain にコードが入ってから。
- 参照の制限では **純粋関数は守りきれない**。`DateTime.Now`、`Guid.NewGuid()` は .NET 本体にあり、参照なしで呼べる。現在時刻は引数で受け取る設計にする。

## AppHost → Api

AppHost は Api のクラスを `new` しない。**別プロセスとして起動し、設定を環境変数で渡す。**

```
AppHost ──① Api を別プロセスとして起動
        ──② 環境変数を渡す：接続文字列、ポート、テレメトリの送り先
Api     ──③ 自分の Program.cs を実行し、自分で待ち受けを始める
```

証拠:

- AppHost の `bin/` に `RoomBooking.Api.dll` がない。普通の参照なら参照先の dll がコピーされる。
- Aspire がビルド時に `Projects.RoomBooking_Api` を生成する（`obj/Debug/net10.0/Aspire/references/RoomBooking_Api.ProjectMetadata.g.cs`）。中身は `.csproj` のパスと `SuppressBuild => true`（AppHost のビルドで一緒にビルド済みなので、起動前に再ビルドしない）。
- `AppHost.cs` の `AddProject<Projects.RoomBooking_Api>("api")` はこのパスを使う。

渡すのは命令ではなく設定なので、Api は環境変数がそろえば AppHost なしでも起動できる。

## Api.Tests → AppHost

`/health` が緑になるには PostgreSQL も動いている必要がある。だから Api ではなく AppHost を参照する。

```
テストプロセス
 │ DistributedApplicationTestingBuilder.CreateAsync<Projects.RoomBooking_AppHost>()
 │   → AppHost.cs の構成（postgres、api）を読み込む
 ├─ StartAsync() ──► Docker で postgres:18 を本当に起動する
 │               └─► Api を別プロセスで起動し、接続文字列を渡す
 ├─ WaitForResourceHealthyAsync("api")
 └─ GET /health → 200
```

- モックではなく本物の PostgreSQL。Dev Container を DinD にした判断（10/25）がここで効く。
- `/health` が DB まで見るのは、Api の `AddNpgsqlDataSource("roombooking")` が DB 接続のヘルスチェックを自動で登録するため。

| | Domain.Tests | Api.Tests |
|---|---|---|
| 必要なもの | .NET | .NET ＋ Docker |
| 1 回の時間 | ミリ秒 | 数秒〜数十秒 |
| 確かめられること | ルールのロジック | DB・設定・配線を含めた全体 |

本数はテストピラミッドどおり。速いテストを多く、結合テストは少なく。

## フェーズ 1 のテストの置き場所

ADR-0001 では、重複の判定を 2 か所で実装する（Domain の事前チェックと DB の排他制約）。

| テスト | 置き場所 | 理由 |
|---|---|---|
| 重なる予約は弾く・接している予約は通す（境界を網羅） | Domain.Tests | ルールのロジック。速いので網羅できる |
| 接している 2 件は両方登録できる（1 本だけ） | Api.Tests | Domain の判定と DB の `'[)'` が同じ境界で合っていることの確認 |
| すり抜けた重複を DB が止める | Api.Tests（保存処理から直接 INSERT） | 排他制約は本物の DB でしか確かめられない |
| 重複は 409 になる | Api.Tests（普通の API テスト） | Domain 検出でも DB 検出でも同じ `ReservationConflict` → 409 |

### Q. 同時リクエストのテストはどう書くか

- 素直に 2 つ同時に投げると、片方が先に終わり、もう片方は Domain の事前チェックで弾かれる。テストは通るが、排他制約は一度も働いていない。
- 回答: Domain をモックにして事前チェックを素通りさせる。
- 補足: 方向は正しいが、モックにすると純粋関数にテストのためだけのインターフェースが要り、本番と違う配線をテストすることになる。事前チェックより **下の層（保存処理）から直接入る** ほうが軽い。
- 本当に同時に投げるテストは、たまにしか失敗しないので信頼できない。書くなら補助的に。

## 未確認・気になる点

- `Api.Tests.csproj` には `<IsTestProject>true</IsTestProject>` があるが、`Domain.Tests.csproj` にはない。`xunit.v3` パッケージが自動で設定する可能性があり、実際の値は未確認（`dotnet msbuild <csproj> -getProperty:IsTestProject` で確認できる）。どちらかにそろえる。
- `Domain.csproj` と `Domain.Tests.csproj` だけ、先頭に BOM（UTF-8 の目印のバイト）が付いている。動作には影響しない。

## 学んだ判断基準

1. 依存は **安定している側に向ける**。テストしやすさは結果としてついてくる。
2. 参照の制限で守れるのは「どのパッケージを使うか」まで。純粋さ（時刻・乱数）は設計で守る。
3. テストしにくいときは、タイミングを再現しようとしない。**確かめたい性質を分けて、それぞれが一番入りやすい層から入る。**
