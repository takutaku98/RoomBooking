# コードリーディング Step 2: AppHost

2026-10-28 / 全ファイル読解の Step 2

## 結論

AppHost は「何を起動して、どうつなぐか」の **構成図（アプリモデル）** を組み立てる。つなぐ手段は **環境変数** だけで、Api 側はそれを標準の名前で読む。

| ファイル | 決めていること |
|---|---|
| `AppHost.cs` | 何を起動して、どうつなぐか（本体） |
| `Properties/launchSettings.json` | AppHost 自身とダッシュボードのポート |
| `aspire.config.json` | `aspire run` がどの AppHost を起動するか |
| `appsettings*.json` | AppHost 自身のログの量 |

## AppHost.cs は手順ではなく構成図

```
[postgres] コンテナ（postgres:18）
    └─ [roombooking] データベース（postgres の中の論理 DB）
            ▲
            │ WithReference  … 接続文字列を渡す
            │ WaitFor        … healthy になるまで起動を待つ
          [api] プロジェクト（ヘルスチェック: /health）
```

- `Add...` / `With...` は構成図を組み立てるだけ。実際の起動は `Run()` で構成図を **DCP**（Developer Control Plane。プロセスとコンテナを起動する係）に渡したとき。`appsettings.json` の `"Aspire.Hosting.Dcp": "Warning"` は DCP のログを減らす設定。
- `AddDatabase` は新しいコンテナを作らない。同じ Postgres の中に DB を 1 つ作る。
- `WithDataVolume()` を付けていないので、DB は起動のたびにまっさら（ADR-0003）。

### Q. IaC か

宣言的に書いてツールに任せる発想は同じ。対象が違う。

| | IaC（Terraform、Bicep） | AppHost |
|---|---|---|
| 主な対象 | クラウドの本番・ステージング | 手元の開発環境 |
| 実行すると | クラウドにリソースが作られる | ローカルでコンテナとプロセスが立ち上がる |

`aspire publish` で、構成図からデプロイ用の成果物（Docker Compose、Bicep など）を生成することもできる。

### Q. WaitFor を消すと何が起きるか

- 回答: ヘルスチェックの実行時に DB 接続に失敗して落ちる。
- 補足: 落ちない。`NpgsqlDataSource` は最初に使われるまで接続しないので、起動は成功する。`/health` は最初は赤で、Postgres の準備ができると緑になる。
- 本当に困るのは **起動時に DB を使う処理**（マイグレーションなど）を入れたとき。その処理が失敗して、Api の起動が失敗する。

## AppHost が Api に渡すもの

AppHost を起動し、Api プロセスの環境変数（`/proc/<pid>/environ`）を実際に確認した。

```
ConnectionStrings__roombooking=Host=localhost;Port=34681;Username=postgres;Password=***;Database=roombooking
OTEL_EXPORTER_OTLP_ENDPOINT=https://localhost:21150
OTEL_SERVICE_NAME=api
ASPNETCORE_URLS=https://localhost:25549;http://localhost:25548
ASPNETCORE_ENVIRONMENT=Development
```

| 環境変数 | 何のため | 読んでいるコード（すべて Api プロセスの中） |
|---|---|---|
| `ConnectionStrings__roombooking` | DB の接続先 | Aspire.Npgsql（`AddNpgsqlDataSource("roombooking")`） |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | テレメトリの送り先 | ServiceDefaults（`Extensions.cs` の `AddOpenTelemetryExporters`） |
| `ASPNETCORE_URLS` | 自分が待ち受けるポート | ASP.NET Core（Kestrel） |
| `ASPNETCORE_ENVIRONMENT` | 環境の名前 | `IsDevelopment()`。`/health` の公開を切り替える |

- .NET の設定システムは、環境変数の `__` を `:` に読み替える。`ConnectionStrings__roombooking` は `appsettings.json` の `ConnectionStrings:roombooking` と同じ場所に入る。
- ServiceDefaults は別プロセスではなく、Api に組み込まれたライブラリ。`Program.cs` に環境変数を読むコードがなくても設定が効くのはそのため。
- Postgres のポートは起動のたびにランダム（`34681`）。固定のポートを書かずに接続文字列で渡すのはこのため。
- `dotnet run` は内部で `aspire run` を呼んでいた（`AspireUseCliBundle` の効果。Step 0）。

### OTLP の送り先が伝わる流れ

```
AppHost の launchSettings
  ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=21150   … ダッシュボードの受け口（AppHost 自身への設定）
        ↓ AppHost が OTel の標準の名前に言い換えて渡す
Api の環境変数
  OTEL_EXPORTER_OTLP_ENDPOINT=21150          … テレメトリの送り先
        ↓
ServiceDefaults: 値があれば UseOtlpExporter()
```

- 回答: AppHost が `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` を渡し、Api は `ASPNETCORE_ENVIRONMENT` から辿る。
- 補足: Api が読むのは OTel 標準の `OTEL_EXPORTER_OTLP_ENDPOINT`。`ASPNETCORE_ENVIRONMENT` は OTLP とは無関係。Api にも `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` が見えるが、親プロセスから引き継がれているだけで、読まれていない。

### Q. 文字列 "roombooking" が 2 か所にあるのは不安か

| 選択肢 | 良い点 | 悪い点 |
|---|---|---|
| A: 文字列のまま | 依存が増えない | 打ち間違いにコンパイルでは気づけない |
| B: 定数の共有プロジェクトを AppHost と Api から参照 | コンパイル時にそろう | Api が「Aspire から起動される」ことを知る |

- 判断: A。この名前は AppHost との約束ではなく、**Api が外部に公開している設定のインターフェース**。本番ではデプロイ先が同じ名前で渡すので、B でも AppHost の経路しか守れない。
- 間違えても `/health` の結合テストが数秒で失敗する。コンパイルで守れないものはテストで素早く検出する。

## 本番では誰が渡すか

- 回答: サーバーが secrets を通して渡す。名前で参照しているので Api のコードの変更は不要。
- これは **The Twelve-Factor App の「III. 設定」**（設定は環境変数に置き、コードから分ける）。

| 環境変数 | 秘密か | 本番で渡すもの（例） |
|---|---|---|
| `ConnectionStrings__roombooking` | 秘密（パスワード） | Key Vault や Kubernetes Secret から環境変数として注入 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | 秘密ではない | コンテナ基盤の普通の環境変数 |
| `ASPNETCORE_URLS` | 秘密ではない | .NET 公式コンテナイメージの既定値（`ASPNETCORE_HTTP_PORTS=8080`） |

## 技術的負債: 本番に /health がない

`MapDefaultEndpoints` は `IsDevelopment()` のときだけ `/health` を公開する。コンテナ基盤はヘルスチェックでコンテナの生死を判断するので、デプロイするときに困る。[TD-001](../tech-debt.md#td-001-本番に-health-がない) として登録した。

## その他の修正

- `Domain.Tests.csproj` に `<IsTestProject>true</IsTestProject>` を追加し、Api.Tests とそろえた（Step 1 の未確認点）。MTP は値がなくてもテストを実行するので実害はなかったが、IDE やカバレッジのツールがこの値を見ることがある。
- 実測: Domain.Tests 0.8 秒、Api.Tests 17.4 秒（Step 1 の「ミリ秒 vs 数十秒」）。

## 学んだ判断基準

1. 設定は **標準の名前** で受け取る（`ConnectionStrings__*`、`OTEL_*`）。渡す側が AppHost でもデプロイ先でも、コードは変わらない。
2. 名前の打ち間違いのようにコンパイルで守れないものは、テストで素早く検出する。
3. 開発環境だけで成り立っている前提（`IsDevelopment()` の分岐）は、本番での扱いを負債として書き残す。
