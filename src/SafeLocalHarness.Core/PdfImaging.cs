namespace SafeLocalHarness.Core;

/// <summary>A recognised word and its box, in pixels of the image it was read from.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>
/// What OCR read from one page image. Japanese words come back one character at a time, so words are joined without
/// spaces, except between two Latin words or numbers ("PAGE-TWO total").
/// </summary>
public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join("\n", Lines.Select(line =>
    {
        var builder = new System.Text.StringBuilder();
        foreach (var word in line.Words)
        {
            if (builder.Length > 0 && word.Text.Length > 0 && char.IsAscii(builder[^1]) && char.IsLetterOrDigit(builder[^1]) && char.IsAscii(word.Text[0]) && char.IsLetterOrDigit(word.Text[0]))
                builder.Append(' ');
            builder.Append(word.Text);
        }
        return builder.ToString();
    }));
}

public sealed record PixelBox(double X, double Y, double Width, double Height);

/// <summary>
/// 4-4b: turning PDF pages into images, reading text from images (OCR) and painting boxes over an image. Implemented
/// with Windows' own PDF renderer and OCR (SafeLocalHarness.Windows); Core only sees this interface, so the rest of
/// LocalStudio stays platform-neutral and testable.
/// </summary>
public interface IPdfImaging
{
    /// <summary>The page (1-based) as a PNG, <paramref name="scale"/> × its size in points (1 = 72 dpi).</summary>
    Task<byte[]> RenderPageAsync(string pdfPath, int page, double scale, CancellationToken cancellationToken = default);

    /// <summary>Japanese OCR of a PNG; throws when the Japanese recogniser is not installed.</summary>
    Task<OcrPage> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default);

    /// <summary>The PNG with solid black boxes painted over the given areas (pixels).</summary>
    Task<byte[]> FillBoxesAsync(byte[] png, IReadOnlyList<PixelBox> boxes, CancellationToken cancellationToken = default);
}
