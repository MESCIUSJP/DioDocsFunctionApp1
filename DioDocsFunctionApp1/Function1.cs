using Azure.Storage.Blobs;
using GrapeCity.Documents.Drawing;
using GrapeCity.Documents.Imaging;
using GrapeCity.Documents.Pdf;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DioDocsFunctionApp1;

/// <summary>
/// PDF ファイルを画像に変換する Azure Function
/// </summary>
public class Function1
{
    private readonly ILogger<Function1> _logger;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="logger">ロギング用のILogger</param>
    public Function1(ILogger<Function1> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// PDFファイルを受け取り、読み込んで各ページをPNG画像に変換、リサイズしてアップロードします
    /// </summary>
    /// <param name="stream">PDFファイルのストリーム</param>
    /// <param name="blobContainerClient">PNG画像を保存するBlobコンテナ</param>
    /// <param name="name">PDFファイルの名前</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    [Function(nameof(Function1))]
    public async Task Run(
    [BlobTrigger("pdf-input/{name}", Source = BlobTriggerSource.EventGrid, Connection = "AzureWebJobsStorage")] Stream stream,
    [BlobInput("image-output", Connection = "AzureWebJobsStorage")] BlobContainerClient blobContainerClient,
    string name,
    CancellationToken cancellationToken)
    {
        _logger.LogInformation("PDFを処理します: {Name}", name);

        // ライセンスキーを設定。設定しない場合はPDFファイルを5ページまでしか処理できません。
        //GcPdfDocument.SetLicenseKey("XXXXX");
        //GcBitmap.SetLicenseKey("XXXXX");

        // PDFファイルを読み込む
        var document = new GcPdfDocument();
        document.Load(stream);

        // PDFファイルにページが無い場合は処理をスキップ
        if (document.Pages.Count == 0)
        {
            _logger.LogWarning("PDFにページがありません。処理をスキップします: {Name}", name);
            return;
        }

        // 出力ファイル名のプレフィックス（拡張子を除いたPDFファイル名）
        var sourceName = Path.GetFileNameWithoutExtension(name);

        // PNG画像へのレンダリングオプションを設定
        var renderOptions = new SaveAsImageOptions
        {
            Resolution = 150,                    // 解像度150DPI
            DrawAnnotations = false,             // 注釈は描画しない
            DrawFormFields = false,              // フォームフィールドは描画しない
            BackColor = System.Drawing.Color.White // 背景色は白
        };

        // 正常に処理されたページ数をカウント
        var successCount = 0;

        // PDFドキュメントの各ページを処理
        for (var pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            var pageNumber = pageIndex + 1;

            try
            {
                // ページをPNG画像として出力ストリームに保存
                await using var renderedPng = new MemoryStream();
                document.Pages[pageIndex].SaveAsPng(renderedPng, renderOptions);
                renderedPng.Position = 0;

                // 出力されたPNG画像を読み込む
                using var source = new GcBitmap();
                source.Load(renderedPng);

                // 画像をスケーリング（最大幅1920pxに制限）
                var scale = Math.Min(1d, 1920 / (double)source.PixelWidth);
                var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
                var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

                // 高品質なCubic補間でリサイズ
                using var resized = source.Resize(width, height, InterpolationMode.Cubic);

                // リサイズしたPNG画像を出力ストリームに保存
                await using var output = new MemoryStream();
                resized.SaveAsPng(output);
                output.Position = 0;

                // BLOBコンテナーにアップロードするファイル名を生成
                // 形式: {PDF名}/page-{ページ番号:4桁}.png
                var blobName = $"{sourceName}/page-{pageNumber:D4}.png";

                // BLOBコンテナーにアップロード
                await blobContainerClient.UploadBlobAsync(blobName, output, cancellationToken);

                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ページの処理に失敗しました: {Name}, ページ番号: {PageNumber}", name, pageNumber);
                throw;
            }
        }

        // 処理完了
        _logger.LogInformation("PDFを {PageCount} ページ処理しました: {Name}", successCount, name);
    }
}
