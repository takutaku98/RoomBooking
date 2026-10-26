# コードリーディング Step 0: 開発環境とビルドの土台

2026-10-27 / 全ファイル読解の Step 0

## 結論

Step 0 の 4 ファイルは、コードを動かす前の「土台」を決めている。

| ファイル | 決めていること |
|---|---|
| `.devcontainer/devcontainer.json` | 開発環境（コンテナ）に何を入れるか |
| `.devcontainer/devcontainer-lock.json` | Feature（インストーラー）のバージョンの固定 |
| `global.json` | `dotnet` コマンドの振る舞い（ここではテストランナー） |
| `Directory.Packages.props` | 全プロジェクトが使う NuGet パッケージのバージョン |

ファイル同士の関係は、**「マシン側の道具」と「プロジェクト側の宣言」の組み合わせ**として読むと分かりやすい。

```
            マシン側（道具）            プロジェクト側（宣言）
.NET    :  .NET SDK 10.0.x      ⇔   net10.0
Aspire  :  aspire CLI（最新）   ⇔   Aspire 13.6.4
           ↑ devcontainer.json       ↑ .csproj / Directory.Packages.props
```

## 用語

- JSON の `"キー": 値` の、コロンの前を **キー**（key）、組を **プロパティ**（RFC 8259 では「メンバー」）と呼ぶ。
- **.NET SDK**: マシンに入れる道具一式（`dotnet` コマンド、コンパイラ、ビルドツール、実行環境）。
- **TFM（Target Framework Moniker）**: `.csproj` の `<TargetFramework>net10.0</TargetFramework>`。「このアプリは .NET 10 向けに作る」という宣言。
- **aspire CLI**: マシンに入れる Aspire のコマンド（`aspire run`、`aspire certs trust`）。
- **Aspire 13.6.4**: プロジェクトが使う Aspire のライブラリと SDK のバージョン。

たとえ: SDK は大工さんの工具箱、TFM は設計図に書かれた建築基準。工具箱が新しければ古い基準も作れる（SDK 10 で `net8.0` もビルドできる）が、古い工具箱では新しい基準を作れない。

## devcontainer.json

Dev Container は 3 段階で組み立てられる。キーは「どの段階で効くか」で整理する。

```
【ビルド時】 image ──► features を上に積む   … コンテナイメージを作る
【起動時】   postStartCommand を実行          … 起動のたびに実行する
【接続時】   remoteEnv / customizations       … VS Code がコンテナにつなぐときに効く
```

| キー | 段階 | 意味 | 値 |
|---|---|---|---|
| `name` | — | VS Code に表示される名前。動作には影響しない | `RoomBooking` |
| `image` | ビルド | 土台の OS イメージ | 汎用 Ubuntu 26.04（.NET は入っていない） |
| `features` | ビルド | 土台に追加する部品。キーが Feature の ID、値がオプション | 下の表 |
| `remoteEnv` | 接続 | VS Code がコンテナ内で起動するプロセスに渡す環境変数 | `SSL_CERT_DIR` |
| `postStartCommand` | 起動 | コンテナの起動のたびに実行するコマンド | `aspire certs trust --non-interactive` |
| `customizations` | 接続 | ツールごとの設定。`vscode.extensions` はコンテナ内に入れる拡張機能 | C# Dev Kit、Aspire 拡張 |

| Feature | 役割 |
|---|---|
| `dotnet:2`（`version: "10.0"`） | .NET 10 SDK を入れる |
| `docker-in-docker:4` | コンテナの中で Docker を動かす |
| `aspire:3` | `aspire` CLI を入れる。バージョンを指定するオプションはなく、ビルド時点の最新安定版が入る |

補足:

- Feature の ID は「レジストリ / 名前空間 / Feature 名 : メジャーバージョン」の形。
- Feature のインストール順は JSON に書いた順とは限らない。ツールが依存関係から決める（`overrideFeatureInstallOrder` で指定できる）。
- `remoteEnv` は VS Code 経由のプロセスにだけ渡り、作り直しは不要。`containerEnv` はコンテナ全体に効き、変えるには作り直しが要る。

### HTTPS 証明書の 3 点セット

```
features."aspire:3"  ──► aspire コマンドが使えるようになる
        ▼
postStartCommand: aspire certs trust
        │  開発用の HTTPS 証明書を作り、~/.aspnet/dev-certs/trust に置く
        ▼
remoteEnv.SSL_CERT_DIR = /usr/lib/ssl/certs : ~/.aspnet/dev-certs/trust
        → Linux 上の .NET（OpenSSL）がこの証明書を信頼する
        → AppHost、ダッシュボード、API の間の HTTPS が証明書エラーにならない
```

Linux には「証明書ストアに登録すれば全部 OK」という共通の仕組みがないため、置き場所を環境変数で教える。3 つのどれが欠けても HTTPS が失敗する。

### Q. なぜ postCreateCommand ではなく postStartCommand か

- 回答: `postCreateCommand` は作成時の 1 回だけなので、期限切れ後に更新されない。
- 補足: 開発用証明書の期限は通常 1 年程度なので、実際に困る場面は少ない。「コストがほぼゼロなので念のため」程度の判断。
- 判断基準: `postStartCommand` に置いてよいのは **冪等** かつ **速い** コマンドだけ。重い処理や冪等でない処理（DB の初期化など）は置かない。

## devcontainer-lock.json

`features` のキーの `:2` は「2.x の最新」という意味で、時期によって中身が変わる。ロックファイルは、実際に使うバージョン（`2.5.0`）と中身のハッシュを固定する（npm の `package-lock.json` と同じ考え方）。ツールが生成するファイルで、手では書かない。

| 書き方 | 許すズレ | 守ってくれるもの |
|---|---|---|
| タグなし（latest） | すべて | なし |
| `:2`（タグだけ） | 2.x の中のズレ | 互換性を壊す変更（3.0）から守られる |
| `:2` ＋ ロックファイル | ズレなし | 全員が同じバージョン。ハッシュで中身のすり替えも検知できる |

### ロックファイルは過剰か

固定しているのは **Feature（インストーラー）だけ** で、中身は固定していない。

| 固定されているもの | 固定されていないもの |
|---|---|
| dotnet Feature 2.5.0 | .NET SDK の 10.0.x のパッチ番号 |
| docker-in-docker Feature 4.1.3 | Docker 本体のバージョン |
| aspire Feature 3.1.0 | Aspire CLI のバージョン |
| — | 土台のイメージ `base:3-ubuntu26.04` |

- このプロジェクト（1 人、CI なし、学習用）では効果は小さい。コストもほぼゼロなので、**残すが、これ以上は固定しない**（YAGNI）。
- チーム開発や CI を入れたら効果が出てくる。
- 代償: セキュリティ修正が自動で入らない。定期的にロックを更新して返す（Dependabot が対応している）。

追加した動機は「ツール間の対応バージョンの差異を受けにくく安全そう」だった。動機は正しいが、手段（ロックファイル）はその半分しか満たしていなかった。

## global.json

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

- `dotnet` コマンド全体の設定。カレントから親ディレクトリへたどって最初に見つけたものが使われる。
- `test.runner`: `dotnet test` の実行エンジンを VSTest ではなく **Microsoft.Testing.Platform（MTP）** にする。
- テストの `.csproj` の `<OutputType>Exe</OutputType>` とセット。xUnit v3 と MTP では、テストプロジェクト自体が実行ファイルになる。
- `sdk.version`（SDK のバージョン固定）は書いていない。インストールされている SDK がそのまま使われる。

## Directory.Packages.props

- **Central Package Management（CPM）**。`ManagePackageVersionsCentrally=true` にすると、各 `.csproj` の `PackageReference` にはバージョンを書かず、ここの `PackageVersion` に一元化する。
- MSBuild が各 `.csproj` から親ディレクトリへたどって自動で読み込む。どこからも明示的に参照されていないのはそのため。
- `Label="AppHost"` などは人間向けの見出しで、動作には影響しない。
- アプリの動作に直接効く固定は、ロックファイルではなく **こちら**。
- 注意: `AppHost.csproj` の `Sdk="Aspire.AppHost.Sdk/13.6.4"` は CPM の管理外。Aspire を更新するときは 2 か所を手でそろえる。

## バージョンのズレのリスク評価

「数字が違う」こと自体は問題ではない。問題は **マシン側がプロジェクト側の要求を満たせない** とき。

| マシン側 | プロジェクト側 | 結果 |
|---|---|---|
| SDK 10.0.5 | `net10.0` | ✅ 要求を満たしている |
| SDK 9.0.x | `net10.0` | ❌ `NETSDK1045` でビルドが失敗する |

リスクは 3 つの掛け算で見積もる。

| 観点 | SDK 10.0.x × net10.0 | aspire CLI × Aspire 13.6.4 |
|---|---|---|
| 変化の頻度 | 毎月（パッチのみ） | 速い |
| 互換性の約束 | 強い（.NET のサービシングポリシー） | 弱め（発展途中） |
| 壊れたときにすぐ気づけるか | すぐ気づける（ビルドが失敗する） | すぐ気づける（`aspire run` が失敗する） |

- 一番ズレやすいのは aspire CLI と Aspire 13.6.4 の組み合わせ。CLI は `AspireUseCliBundle`（AppHost.csproj）、`aspire.config.json`、`postStartCommand` から使われている。
- ただし誇張は禁物。CLI は古い AppHost もサポートする方針で、壊れても起動時にすぐ気づける。
- 人間側のズレ: `Aspire.AppHost.Sdk`（CPM 管理外）と `Aspire.Hosting.*`（CPM 管理下）の更新漏れ。

## 学んだ判断基準

固定するか迷ったら、「安全そう」という感覚を 3 つの問いに分解する。

1. **どのツールとどのツールの組み合わせ** が壊れうるか？
2. 選んだ手段は、**その組み合わせを実際に固定しているか？**
3. 固定のコスト（更新の手間、セキュリティ修正の遅れ）に見合うか？

①を先に具体的にしてから手段を選ぶ。リスクは「変化の頻度 × 互換性の約束 × 壊れたときに気づけるか」で見積もる。
