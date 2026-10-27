# Flyway スパイク Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** AppHost の起動だけで Flyway が V1 を流し、排他制約が重なる予約を 23P01 で弾くことを自動テストで確かめる。

**Architecture:** CommunityToolkit の Flyway 統合で Flyway をコンテナとして起動し、`roombooking` DB にマイグレーションを流す。Api は Flyway の完了を待ってから起動する。Api.Tests は AppHost をアセンブリフィクスチャで 1 回だけ起動し、テストごとに専用の会議室 ID でデータを分ける。

**Tech Stack:** Aspire 13.6.4、`CommunityToolkit.Aspire.Hosting.PostgreSQL.Extensions` 13.6.1（`CommunityToolkit.Aspire.Hosting.Flyway` 13.6.1 を含む）、PostgreSQL 18、Npgsql 10.0.3、xUnit v3 4.0.2

**Spec:** ADR-0001（排他制約の定義）、ADR-0003（Flyway の採用とフォローアップ）、`docs/code-reading/step4-tests.md`（フィクスチャとデータの分け方）。完了条件は下の「Global Constraints」。

## Global Constraints

- **タイムボックス: 4 時間。** 超えたら手を止め、Postgres の初期化スクリプトに切り替えるか続けるかを判断する（切り替える場合は ADR-0003 を置き換える ADR を書く）。
- 完了条件 1: `dotnet run --project src/RoomBooking.AppHost` だけで、Flyway が migrate して正常終了し、その後に Api が起動してダッシュボードが緑になる。
- 完了条件 2: V1 の SQL は `reservations` テーブルの 4 列（`id`、`room_id`、`start_at`、`end_at`）と排他制約だけ。ほかの列・テーブルは作らない（ドメインより先にスキーマを作り込まない）。
- 完了条件 3: 重なる 2 件の INSERT は 2 件目が SQLSTATE `23P01` で失敗し、ちょうど接する 2 件は両方入ることを Api.Tests で自動テストする。
- 排他制約は ADR-0001 のとおり: `EXCLUDE USING gist (room_id WITH =, tstzrange(start_at, end_at, '[)') WITH &&)`。`btree_gist` 拡張が必要。
- マイグレーションの SQL は `db/migrations/` に置く（ADR-0003 のフォローアップの例のとおり）。
- パッケージのバージョンは `Directory.Packages.props`（CPM）に書く。`.csproj` にはバージョンを書かない。
- ビルドとテストは Dev Container の中で実行する。

## Review Focus

1. 別の会議室なら同じ時間帯でも入る（`room_id WITH =` が効いている）。→ Task 2 に `DifferentRooms_SameSlot_BothSucceed` を入れる。
2. 長さゼロの予約（開始 = 終了）は空の区間になり、どの予約とも重ならないので排他制約をすり抜ける。→ V1 に `CHECK (start_at < end_at)` を入れ、Task 2 に `EmptySlot_IsRejected`（SQLSTATE `23514`）を入れる。
3. テストの実行順で結果が変わる（共有 DB）。→ すべてのテストが `Guid.NewGuid()` の会議室を使う。Task 2 の全テストで守る。
4. Flyway が失敗したのに Api が起動して緑になる。→ Api に `WaitForCompletion(flyway)` を付ける。Task 2 Step 6 で、わざと壊した SQL で Api が起動しないことを一度だけ手で確かめる（テストにはしない）。
5. Flyway のコンテナからマイグレーションの SQL が見えない（パスの解決が AppHost のディレクトリ基準かどうか）。→ Task 2 Step 5 のダッシュボードと Flyway のログで確かめる。

---

### Task 1: AppHost をアセンブリフィクスチャで共有する

Api.Tests が 2 本目になるので、Step 4 の決定どおりフィクスチャを入れる。振る舞いは変えない（既存の `/health` テストが緑のまま）。

**Files:**
- Create: `tests/RoomBooking.Api.Tests/AppHostFixture.cs`
- Modify: `tests/RoomBooking.Api.Tests/HealthCheckTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AppHostFixture : IAsyncLifetime`
    - `public DistributedApplication App { get; }` — 起動済みで、`"api"` が Healthy になっている
  - `[assembly: AssemblyFixture(typeof(AppHostFixture))]`（`AppHostFixture.cs` の先頭）
  - テストクラスはコンストラクタで `AppHostFixture fixture` を受け取る

- [ ] **Step 1: `AppHostFixture` を作る**

`InitializeAsync` に、今の `HealthCheckTests` の Arrange（`CreateAsync` → `BuildAsync` → `StartAsync` → `WaitForResourceHealthyAsync("api")`、各段に 30 秒の `WaitAsync`）を移す。`TestContext.Current.CancellationToken` は使わず `CancellationToken.None` にする（フィクスチャはテストより長く生きるため）。`DisposeAsync` で `App` を片付ける。

- [ ] **Step 2: `HealthCheckTests` をフィクスチャを使う形に書き換える**

テスト名と Assert（`HttpStatusCode.OK`）はそのまま。Arrange は `fixture.App.CreateHttpClient("api")` だけになる。

- [ ] **Step 3: テストを実行する**

Run: `dotnet test --project tests/RoomBooking.Api.Tests`
Expected: `succeeded: 1`、`failed: 0`

- [ ] **Step 4: コミット**

```bash
git add tests/RoomBooking.Api.Tests/AppHostFixture.cs tests/RoomBooking.Api.Tests/HealthCheckTests.cs
git commit -m "test: Api.Tests の AppHost をアセンブリフィクスチャで共有"
```

### Task 2: Flyway で V1 を流し、排他制約をテストする

**Files:**
- Create: `tests/RoomBooking.Api.Tests/ReservationExclusionConstraintTests.cs`
- Create: `db/migrations/V1__create_reservations.sql`
- Modify: `Directory.Packages.props`（`Label="AppHost"` に `CommunityToolkit.Aspire.Hosting.PostgreSQL.Extensions` 13.6.1、`Label="Tests"` に `Npgsql` 10.0.3）
- Modify: `src/RoomBooking.AppHost/RoomBooking.AppHost.csproj`、`src/RoomBooking.AppHost/AppHost.cs`
- Modify: `tests/RoomBooking.Api.Tests/RoomBooking.Api.Tests.csproj`（`Npgsql` を追加）
- Modify: `docs/adr/0003-schema-migration-with-flyway.md`（フォローアップのチェック）

**Interfaces:**
- Consumes: `AppHostFixture.App`（Task 1）
- Produces: Flyway リソース名 `"flyway"`、テーブル `reservations(id uuid, room_id uuid, start_at timestamptz, end_at timestamptz)`、制約名 `reservations_no_overlap`（EXCLUDE）と `reservations_slot_not_empty`（CHECK）

- [ ] **Step 1: 失敗するテストを書く**

接続文字列は `await fixture.App.GetConnectionStringAsync("roombooking")` で取り、`NpgsqlDataSource` で直接 INSERT する。各テストは `Guid.NewGuid()` の会議室を使う。時刻は `2026-11-02` の UTC で固定する。

```csharp
[Fact] public async Task OverlappingSlots_SameRoom_SecondInsertFailsWithExclusionViolation()
// 10:00-11:00 を入れたあと 10:30-11:30 → PostgresException、SqlState == "23P01"、ConstraintName == "reservations_no_overlap"

[Fact] public async Task AdjacentSlots_SameRoom_BothSucceed()
// 10:00-11:00 と 11:00-12:00 → 例外なし。その会議室の件数 == 2

[Fact] public async Task DifferentRooms_SameSlot_BothSucceed()
// 会議室 A と B に 10:00-11:00 → 例外なし

[Fact] public async Task EmptySlot_IsRejected()
// 10:00-10:00 → PostgresException、SqlState == "23514"、ConstraintName == "reservations_slot_not_empty"
```

- [ ] **Step 2: テストが失敗することを確かめる**

Run: `dotnet test --project tests/RoomBooking.Api.Tests`
Expected: 新しい 4 本が FAIL（`42P01: relation "reservations" does not exist`）。`/health` は PASS。

- [ ] **Step 3: V1 の SQL を書く**

`db/migrations/V1__create_reservations.sql` に、`CREATE EXTENSION IF NOT EXISTS btree_gist;`、4 列の `reservations`（`id` は主キー、全列 `NOT NULL`）、`CONSTRAINT reservations_slot_not_empty CHECK (start_at < end_at)`、`CONSTRAINT reservations_no_overlap EXCLUDE USING gist (room_id WITH =, tstzrange(start_at, end_at, '[)') WITH &&)`。

- [ ] **Step 4: AppHost に Flyway を組み込む**

`AppHost.cs` で `builder.AddFlyway("flyway", <db/migrations へのパス>)` を作り、`database.WithFlywayMigration(flyway)`、`flyway.WaitFor(database)`、api に `.WaitForCompletion(flyway)` を足す。パスは AppHost のディレクトリ基準と想定して `"../../db/migrations"` から試す（Review Focus 5）。ADR-0003 を指す日本語のコメントを 1 行付ける（既存の postgres のコメントと同じ形）。

- [ ] **Step 5: AppHost を起動して完了条件 1 を確かめる**

Run: `dotnet run --project src/RoomBooking.AppHost`
Expected: ダッシュボードで `flyway` が Finished（終了コード 0）、`api` が Running かつ Healthy。Flyway のログに `Successfully applied 1 migration`。

- [ ] **Step 6: Flyway が失敗したら Api が起動しないことを一度だけ確かめる**

V1 の SQL に一時的に構文エラーを入れて起動し、`flyway` が失敗、`api` が待機のまま起動しないことを確認する。確認したら元に戻す（コミットしない）。

- [ ] **Step 7: テストを実行する**

Run: `dotnet test`
Expected: Api.Tests `succeeded: 5`、Domain.Tests `succeeded: 1`、`failed: 0`

- [ ] **Step 8: ADR-0003 のフォローアップを更新する**

「AppHost で Flyway を動かす方法を決める」「SQL ファイルの置き場所を決める」「排他制約を Flyway の SQL で書けることを確かめる」「Flyway のコンテナから Postgres に接続できることを確かめる」にチェックを付け、採用したパッケージ名とパスを 1 行ずつ書く。

- [ ] **Step 9: コミット**

```bash
git add Directory.Packages.props db/migrations/V1__create_reservations.sql \
  src/RoomBooking.AppHost/RoomBooking.AppHost.csproj src/RoomBooking.AppHost/AppHost.cs \
  tests/RoomBooking.Api.Tests/RoomBooking.Api.Tests.csproj tests/RoomBooking.Api.Tests/ReservationExclusionConstraintTests.cs \
  docs/adr/0003-schema-migration-with-flyway.md
git commit -m "feat: Flyway で V1（予約テーブルと排他制約）を流し、制約をテストで確かめる"
```
