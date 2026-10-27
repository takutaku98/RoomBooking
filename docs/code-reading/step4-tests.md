# コードリーディング Step 4: テスト

2026-10-28 / 全ファイル読解の Step 4

## 結論

`HealthCheckTests` は、AppHost.cs と同じ起動の手順を 1 段ずつ自分の手で進め、各段に 30 秒の上限を付けている。テストが増えたら AppHost をフィクスチャで共有し、テスト同士はデータ（会議室）で分ける。

| ファイル | 中身 |
|---|---|
| `Api.Tests/HealthCheckTests.cs` | AppHost ごと起動して `/health` が 200 を返すかを見る結合テスト |
| `Domain.Tests/UnitTest1.cs`、`Domain/Class1.cs` | 空のテストと空のクラス。フェーズ 0 の完了条件（テストがコンテナ内で実行できる）のための仮置き。フェーズ 1 で本物に置き換える |

空のテストは何も確かめていないが、「テストを見つけて、ビルドして、実行する」仕組みが動くことは確かめている。

## AppHost.cs との対応

```
AppHost.cs（普段の起動）                       HealthCheckTests.cs
──────────────────────────────────────────────────────────────────
DistributedApplication.CreateBuilder(args)     ② CreateAsync<Projects.RoomBooking_AppHost>()
builder.AddPostgres(...) ...                      （②の中で実行される）
builder.Build()                                ③ BuildAsync()
       .Run();                                 ④ StartAsync() ＋ ⑤ WaitForResourceHealthyAsync()
```

## Arrange の各行

| 番号 | コード | 意味 |
|---|---|---|
| ① | `TestContext.Current.CancellationToken` | xUnit v3 が渡す「中止の合図」の札。テストの実行が中止されると合図が出て、札を渡した処理が止まる |
| ② | `DistributedApplicationTestingBuilder.CreateAsync<Projects.RoomBooking_AppHost>()` | AppHost.cs のコードを実行し、`builder.Build()` の直前で横取りする。構成図ができた状態で止まり、**まだ何も起動しない**。テスト用に設定を足すこともできる |
| ③ | `await using var app = await appHost.BuildAsync()` | 構成図を確定する（起動はしない）。`await using` でメソッドを抜けるときに片付けられ、コンテナとプロセスが止まる |
| ④ | `app.StartAsync()` | DCP に渡して起動する。`Run()` と違い、起動の指示が終わると制御がテストに戻る。**起動した ≠ 準備ができた** |
| ⑤ | `ResourceNotifications.WaitForResourceHealthyAsync("api")` | api が Healthy（AppHost.cs の `WithHttpHealthCheck("/health")`）になるまで待つ。ないと DB の準備前に叩いて 503 になり、不安定なテスト（flaky test）になる |

- `.WaitAsync(DefaultTimeout, ...)` は **その 1 行の await だけ** に対する 30 秒の上限。テスト全体や合計の上限ではない。
- `WaitAsync` は待つのを諦めるだけで、処理そのものは止めない。止めるのは ① の札。だから両方を渡す。
- Act の `app.CreateHttpClient("api")` は、名前だけで api の受付の URL を引く。構成図に窓口の情報があるため（Step 3 の DCP の受付）。

### Q. WaitAsync がなかったら、Postgres の起動が止まったときどうなるか

- 回答: 永遠に待ち続け、テストが終わらない。
- 補足: CI ではジョブの上限時間（GitHub Actions の既定は 6 時間）まで止まり、どこで止まったかも分からない。タイムアウトは **早く、場所が分かる形で失敗させる** ためのもの。

## テストが増えたとき

### Q. Api.Tests にテストを 10 本足すと何が困るか

- 回答: 30 秒でタイムアウトし、途中で終了してしまう。
- 補足: 30 秒は 1 行ごとの上限なので、打ち切られはしない。困るのは時間。xUnit はテストごとにクラスを作り直すので、準備（Postgres の起動、約 15 秒）がテストの本数だけくり返される。10 本で 2〜3 分。

### 解決策: フィクスチャ（準備を 1 回にして使い回す）

| 共有の範囲 | xUnit の仕組み |
|---|---|
| 同じテストクラスの中 | `IClassFixture<T>` |
| 複数のクラス | `ICollectionFixture<T>` |
| テストプロジェクト全体 | アセンブリフィクスチャ（xUnit v3） |

- 判断: 今は `/health` の 1 本だけなので入れない（YAGNI）。フェーズ 1 で Api.Tests が 2 本目になったときに入れる。

### Q. DB を共有すると、テスト同士で何が起きるか

テスト A「会議室 1 の 10:00〜11:00 を予約できる」と、テスト B「会議室 1 の 10:30〜11:30 を予約できる」の場合。

- 回答: 実行順によって A か B のどちらかが失敗し、全件が緑になることはない。避けるには、テストごとに専用の DB を用意する。
- 補足: どちらが落ちるかが実行のたびに変わるので、原因が分かりにくい。また、フィクスチャで共有した Api は、起動時に受け取った 1 つの接続文字列の DB にしかつながらない（Step 2）。テストごとの DB は、Api 経由のテストでは使えない。

| 方法 | Api 経由のテスト | 直接 INSERT のテスト（Step 1） | コスト |
|---|---|---|---|
| A: テストごとに別の DB | ✕ Api が 1 つの DB にしかつながらない | ○ | DB の作成とマイグレーション |
| **B: テストごとにデータを分ける**（専用の会議室） | ○ | ○ | ほぼゼロ |
| C: テストのたびに掃除する | △ 順番に流す必要がある | △ | 並列に実行できない |

- 判断: B。データは溜まるが、DB は起動のたびにまっさら（ADR-0003）なので、溜まるのは 1 回の実行の間だけ。

## 学んだ判断基準

1. 待つ処理には上限を付け、**早く、場所が分かる形で** 失敗させる。
2. 「起動した」と「準備ができた」は別。準備ができたことを確認してから叩く。
3. 共有するもの（AppHost、DB）は、テストが互いに触らないように **分ける単位をデータまで小さくする**。共有をやめるのは確実だがコストが高い。
