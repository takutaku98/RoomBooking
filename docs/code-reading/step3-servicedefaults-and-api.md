# コードリーディング Step 3: ServiceDefaults と API

2026-10-28 / 全ファイル読解の Step 3

## 結論

`Program.cs` は「登録」と「パイプライン」の 2 段階で、ServiceDefaults はその両方に 1 つずつ共通の土台を差し込む。ヘルスチェックは「再起動すれば直るか」で 2 つに分かれ、ポートは DCP が受付（リバースプロキシ）として取り次ぐ。

| ファイル | 決めていること |
|---|---|
| `ServiceDefaults/Extensions.cs` | どのサービスにも共通の土台（テレメトリ、ヘルスチェック、HttpClient の既定） |
| `Api/Program.cs` | Api の起動の手順 |
| `Api/Properties/launchSettings.json` | Api のポート（AppHost から起動すると **受付の番号** として使われる） |
| `Api/appsettings*.json` | ログの量、`AllowedHosts` |
| `Api/RoomBooking.Api.http` | 手で叩くリクエスト集（`5263` の `/health`） |

## Program.cs の 2 段階

```csharp
var builder = WebApplication.CreateBuilder(args);
// ── ① 登録: 何を使うかを DI コンテナに登録する ──
builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("roombooking");
builder.Services.AddOpenApi();

var app = builder.Build();                // ここで登録が締め切られる
// ── ② パイプライン: リクエストがどの順で何を通るか ──
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();
app.MapDefaultEndpoints();               // /health と /alive
app.Run();
```

| Extensions.cs のメソッド | 段階 | やっていること |
|---|---|---|
| `ConfigureOpenTelemetry` | ① | ログ・メトリクス・トレースを集める。`/health` と `/alive` はトレースから除外 |
| `AddOpenTelemetryExporters` | ① | `OTEL_EXPORTER_OTLP_ENDPOINT` があれば送る（Step 2） |
| `AddDefaultHealthChecks` | ① | 常に Healthy の `"self"` を `live` タグ付きで登録 |
| `AddServiceDiscovery`、`AddStandardResilienceHandler` | ① | 別サービスを呼ぶ HttpClient の既定。今の Api は誰も呼ばないので未使用 |
| `MapDefaultEndpoints` | ② | `/health` と `/alive` を公開（Development のときだけ。[TD-001](../tech-debt.md#td-001-本番に-health-がない)） |

## using なしで AddServiceDefaults() が呼べる理由

```
Api.csproj: <ImplicitUsings>enable</ImplicitUsings>
   ↓ ビルド時に obj/.../RoomBooking.Api.GlobalUsings.g.cs を生成
global using Microsoft.Extensions.Hosting;   … 全ファイルに using が付いたのと同じ
   ↓
Extensions.cs: namespace Microsoft.Extensions.Hosting;   … わざとここに置いている
```

- 回答: namespace が同じだから。
- 補足: `Program.cs` はトップレベルステートメントで namespace を持たない。正確には「**最初から using されている Microsoft の名前空間に間借りしている**」。

| | 良い点 | 悪い点 |
|---|---|---|
| `Microsoft.Extensions.Hosting` に置く（今） | `using` 不要。どのサービスでも同じ 1 行 | 自分のコードが Microsoft のコードに見える |
| `RoomBooking.ServiceDefaults` に置く | 出どころが明確 | 使う側に `using` が要る |

テンプレートの既定のままでよい。自分の拡張メソッドでは真似しない。

## /alive と /health（liveness と readiness）

コンテナ基盤はアプリに 2 つの別の質問をする。

| 質問 | 名前 | 「いいえ」のとき基盤がすること | このテンプレート |
|---|---|---|---|
| プロセスは生きているか | liveness | **再起動** する | `/alive`（`live` タグだけ = `"self"`） |
| リクエストを受けられるか | readiness | **リクエストを流さない**（再起動しない） | `/health`（すべて = `"self"` ＋ DB） |

### Q. DB のチェックを /alive に入れたら、DB が 5 分落ちたとき何が起きるか

```
DB が落ちる → /alive が Unhealthy → 再起動 → 起動しても DB は落ちたまま → また再起動 …
```

- 回答: DB は直らない。無限ループになる（Kubernetes では CrashLoopBackOff）。
- 被害: 直らない再起動に CPU とメモリを使う。DB の復旧の瞬間に全台が一斉に接続しにくる（サンダリングハード）。ログが再起動で埋まり原因が見えにくい。
- 判断基準: **liveness には「再起動すれば直るもの」（自分自身の状態）だけを入れる。** 依存先は readiness に入れる。

## ポートが合わなくても動く理由（DCP はリバースプロキシでもある）

AppHost を起動して実際に確認した。

| | ポート |
|---|---|
| Api の `launchSettings.json` | `https://localhost:7280`、`http://localhost:5263` |
| Api プロセスの `ASPNETCORE_URLS` | `https://localhost:12716`、`http://localhost:12715`（起動のたびに変わる） |

```
ss -ltnp の結果
127.0.0.1:7280   ← dcp
127.0.0.1:5263   ← dcp
127.0.0.1:12716  ← RoomBooking.Api
127.0.0.1:12715  ← RoomBooking.Api

curl https://localhost:7280/health → 200
```

```
curl ──► 7280番（DCP の受付） ──► 12716番（Api 本人）
curl ◄──  DCP が返事をそのまま渡す  ◄── 200 OK
```

- DCP は「プロセスとコンテナを起動する係」であり、**受付（リバースプロキシ）** でもある。
- `launchSettings.json` の番号は、AppHost から起動すると受付の番号になる。Api 本人の番号は起動のたびに変わるが、受付が吸収するので `.http` を書き換えなくてよい。

### Q. Api を 2 台にするには

- `aspire run` を 2 回実行すると、AppHost・DCP・Postgres・ダッシュボードが丸ごと 2 セットになる（受付のポートもぶつかる）。「Api が 2 台」ではなく「環境が 2 つ」。
- 正しくは構成図に書く: `AddProject<...>("api").WithReplicas(2)`。1 つの DCP が Api を 2 つ起動し、1 つの受付が振り分ける（未検証。振り分けの順番などは動かして確かめていない）。
- 本番では DCP の代わりに、コンテナ基盤のロードバランサー（Kubernetes の Service、Azure Container Apps の Ingress など）が受付になる。Api は自分が何台かを知らない。

### Q. Api が 2 台のとき、Api の中の lock で重複予約を防げるか

```
Api その1 のメモリ            Api その2 のメモリ
  lock(その1のカギ)              lock(その2のカギ)
  A: チェック OK                 B: チェック OK      ← 互いのカギが見えない
  A: 保存                        B: 保存             → 重複が 2 件
         └──────── 同じ DB ────────┘
```

- 回答: 防げない。A がチェックを通ってから保存が終わるまでの間に、B がチェックから保存まで終えてしまう。
- 補足: `lock` は同じプロセスの中の順番待ちしかさせられない。1 台なら動くので、本番で台数を増やすまで気づけない。
- 判断基準: **不変条件の保証は、全員が共有している場所（DB）に置く。** ADR-0001 の排他制約は、台数を増やしても壊れない。

## 学んだ判断基準

1. liveness には再起動で直るものだけ。依存先は readiness。
2. 不変条件の保証は、全インスタンスが共有している場所に置く。プロセスの中のロックは台数を増やすと黙って壊れる。
3. 固定の番号は受付（プロキシ）に持たせ、裏の番号の変化を吸収させる。本番ではロードバランサーが同じ役をする。
