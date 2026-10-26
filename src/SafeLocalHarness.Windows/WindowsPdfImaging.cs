using System.Runtime.InteropServices.WindowsRuntime;
using SafeLocalHarness.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SafeLocalHarness.Windows;

/// <summary>
/// PDF pages to images with Windows.Data.Pdf, Japanese OCR with Windows.Media.Ocr, and black boxes painted on the
/// pixels. Everything runs on this PC with Windows' own components: nothing is downloaded or sent anywhere.
/// </summary>
public sealed class WindowsPdfImaging : IPdfImaging
{
    /// <summary>Windows OCR reads images up to this many pixels on a side.</summary>
    public static int MaxOcrDimension => (int)OcrEngine.MaxImageDimension;

    /// <summary>True when Windows has the Japanese OCR recogniser (an optional Windows feature).</summary>
    public static bool JapaneseOcrAvailable => OcrEngine.AvailableRecognizerLanguages.Any(language => language.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));

    public async Task<byte[]> RenderPageAsync(string pdfPath, int page, double scale, CancellationToken cancellationToken = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(cancellationToken);
        var document = await global::Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
        if (page < 1 || page > document.PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        using var pdfPage = document.GetPage((uint)(page - 1));
        // Windows.Data.Pdf gives the page size in DIPs (1/96 inch), not PDF points (1/72 inch): without this conversion
        // the picture was 4/3 too large and redaction boxes landed 25% off target (drill 2026-10-12).
        var points = (Width: pdfPage.Size.Width * 72 / 96, Height: pdfPage.Size.Height * 72 / 96);
        // Keep within what OCR can read, whatever the page size.
        var width = Math.Min(points.Width * scale, MaxOcrDimension);
        var height = Math.Min(points.Height * scale, MaxOcrDimension);
        var factor = Math.Min(width / points.Width, height / points.Height);
        using var stream = new InMemoryRandomAccessStream();
        await pdfPage.RenderToStreamAsync(stream, new global::Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(points.Width * factor)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(points.Height * factor)),
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
        }).AsTask(cancellationToken);
        return await ReadAllAsync(stream, cancellationToken);
    }

    public async Task<OcrPage> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("ja"))
                     ?? throw new InvalidOperationException("このWindowsには日本語の文字認識（OCR）が入っていません。Windowsの設定の「言語」で日本語の「光学式文字認識」を追加してください。");
        using var bitmap = await DecodeAsync(png, cancellationToken);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var lines = result.Lines.Select(line => new SafeLocalHarness.Core.OcrLine(line.Text,
            line.Words.Select(word => new SafeLocalHarness.Core.OcrWord(word.Text, word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height)).ToArray())).ToArray();
        return new OcrPage(bitmap.PixelWidth, bitmap.PixelHeight, lines);
    }

    public async Task<byte[]> FillBoxesAsync(byte[] png, IReadOnlyList<PixelBox> boxes, CancellationToken cancellationToken = default)
    {
        using var bitmap = await DecodeAsync(png, cancellationToken);
        var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
        var pixels = new byte[width * height * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        foreach (var box in boxes)
        {
            var left = Math.Clamp((int)Math.Floor(box.X), 0, width);
            var top = Math.Clamp((int)Math.Floor(box.Y), 0, height);
            var right = Math.Clamp((int)Math.Ceiling(box.X + box.Width), 0, width);
            var bottom = Math.Clamp((int)Math.Ceiling(box.Y + box.Height), 0, height);
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                {
                    var index = (y * width + x) * 4;
                    pixels[index] = 0; pixels[index + 1] = 0; pixels[index + 2] = 0; pixels[index + 3] = 255;
                }
        }
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
        return await ReadAllAsync(stream, cancellationToken);
    }

    private static async Task<SoftwareBitmap> DecodeAsync(byte[] png, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0);
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)stream.Size);
        var read = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None).AsTask(cancellationToken);
        return read.ToArray();
    }
}
