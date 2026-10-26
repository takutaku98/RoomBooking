# 手順書: 依存関係のバージョンを更新する

## 結論

依存は **一緒に上げなければならないグループ** 単位で、1 グループずつ更新する。バージョンの数字は設定ファイルが正で、この手順書には **書かれている場所** だけを載せる。

## 共通の手順

1. ブランチを切る。
2. 下の表から、上げるグループを 1 つ選ぶ。
3. そのグループの「書かれている場所」をすべて直す。
4. 「確認方法」を実行する。
5. グループ単位でコミットする。次のグループは 2 に戻る。

まとめて上げると、壊れたときにどのグループが原因か切り分けられない。

## バージョンが書かれている場所

| グループ | 書かれている場所 | 一緒に動かすもの | 確認方法 |
|---|---|---|---|
| .NET | 各 `.csproj` の `<TargetFramework>`、`.devcontainer/devcontainer.json` の `dotnet` Feature の `version`、`Directory.Packages.props` の `Microsoft.AspNetCore.OpenApi`・`Microsoft.Extensions.*` | メジャーを上げるときは全部 | Dev Container を作り直し、`dotnet build` と `dotnet test` |
| Aspire | `Directory.Packages.props` の `Aspire.*`、`src/RoomBooking.AppHost/RoomBooking.AppHost.csproj` の `Sdk="Aspire.AppHost.Sdk/..."`、コンテナ内の `aspire` CLI | 全部 | `dotnet test`（`HealthCheckTests`）、`aspire run` |
| OpenTelemetry | `Directory.Packages.props` の `OpenTelemetry.*` | 全部 | 自動テストなし。`aspire run` でダッシュボードを開き、API のトレースとメトリクスが見えるか目で確かめる |
| xUnit | `Directory.Packages.props` の `xunit.v3`、`global.json`（テストランナー） | — | `dotnet test` が全件実行されること |
| PostgreSQL | `src/RoomBooking.AppHost/AppHost.cs` の `WithImageTag(...)` | `Aspire.Npgsql`（Npgsql）との互換性 | 結合テスト。Flyway の SQL と `btree_gist` 拡張が動くこと |
| Dev Container | `.devcontainer/devcontainer.json` の `image`・`features`、`.devcontainer/devcontainer-lock.json` | ロックファイルの再生成 | Dev Container を作り直し、`dotnet build` と `dotnet test` |

注意:

- `Aspire.AppHost.Sdk` は Central Package Management の管理外。`Directory.Packages.props` だけ直すと取り残される。
- PostgreSQL のバージョンは設定ファイルではなく C# のコード（`AppHost.cs`）にある。
- `aspire` CLI は Dev Container の Feature で入り、バージョンを指定できない。作り直した時点の最新の安定版になる。

更新後、古いバージョンが残っていないかを検索で確かめる（例: Aspire 13.6.4 から上げたとき）。

```bash
grep -rn "13\.6\.4" --include="*.props" --include="*.csproj" .
```

## PostgreSQL のメジャーバージョン更新

| 環境 | 影響 | 対応 |
|---|---|---|
| 開発 | データはボリュームに残さず、起動のたびに作り直す。データ移行は不要 | SQL と拡張機能の互換性を結合テストで確かめる |
| 本番 | データファイルの形式が変わるため、`pg_upgrade` かダンプとリストアが必要 | 本番環境ができたら手順を追加する |

## この手順書の保守

依存（パッケージ、コンテナイメージ、Feature）を追加したら、上の表に 1 行足す。バージョンの数字は書かない。
