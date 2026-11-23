using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

const int maxFiles = 10;
const int maxUploadBytes = 25 * 1024 * 1024;
const int maxTextBytes = 64 * 1024;
const int maxRequestBytes = maxUploadBytes + 128 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxRequestBytes);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/convert", async (HttpRequest request, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (request.ContentLength > maxRequestBytes)
        return Error("The upload is too large. Choose up to 25 MB of files.", 413);

    if (!request.HasFormContentType ||
        !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        return Error("Choose files using the upload form.");

    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var files = form.Files;
        if (files.Count is 0 or > maxFiles || files.Any(file => file.Name != "files"))
            return Error("Choose up to 10 JPG or PNG images, or one TXT file.");

        if (files.Any(file => file.Length == 0))
            return Error("One of the selected files is empty.");

        if (files.Sum(file => file.Length) > maxUploadBytes)
            return Error("The upload is too large. Choose up to 25 MB of files.", 413);

        var extensions = files.Select(file => Path.GetExtension(file.FileName).ToLowerInvariant()).ToArray();
        var isText = files.Count == 1 && extensions[0] == ".txt";
        if (!isText && extensions.Any(extension => extension is not (".jpg" or ".jpeg" or ".png")))
            return Error("Choose JPG or PNG images together, or convert one TXT file separately.");

        if (isText && files[0].Length > maxTextBytes)
            return Error("Choose a TXT file no larger than 64 KB.", 413);

        using var document = new PdfDocument();
        if (isText)
        {
            using var stream = files[0].OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var encoding = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })
                ? new UnicodeEncoding(false, true, true)
                : bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })
                    ? new UnicodeEncoding(true, true, true)
                    : (Encoding)new UTF8Encoding(false, true);
            var content = encoding.GetString(bytes).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(content) ||
                content.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                return Error("Choose a TXT file containing readable text.");

            AddTextPages(document, content, cancellationToken);
        }
        else
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = files[index].OpenReadStream();
                using var data = new MemoryStream();
                await input.CopyToAsync(data, cancellationToken);
                data.Position = 0;
                using var decoded = Image.FromStream(data, useEmbeddedColorManagement: false, validateImageData: false);
                var expectedFormat = extensions[index] == ".png" ? ImageFormat.Png : ImageFormat.Jpeg;
                if (decoded.RawFormat.Guid != expectedFormat.Guid)
                    return Error("An image does not match its file extension. Choose a valid JPG or PNG.");

                if ((long)decoded.Width * decoded.Height > 20_000_000)
                    return Error("Choose images with no more than 20 megapixels each.");

                var page = document.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                if (decoded.Width > decoded.Height)
                    page.Orientation = PdfSharp.PageOrientation.Landscape;

                using var image = XImage.FromGdiPlusImage(decoded);
                using var graphics = XGraphics.FromPdfPage(page);
                const double margin = 32;
                var scale = Math.Min((page.Width.Point - 2 * margin) / decoded.Width,
                    (page.Height.Point - 2 * margin) / decoded.Height);
                var width = decoded.Width * scale;
                var height = decoded.Height * scale;
                graphics.DrawImage(image, (page.Width.Point - width) / 2, (page.Height.Point - height) / 2, width, height);
            }
        }

        using var output = new MemoryStream();
        document.Save(output, false);
        return Results.File(output.ToArray(), "application/pdf", isText ? "text.pdf" : "images.pdf");
    }
    catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
    {
        return Error("The upload is too large. Choose up to 25 MB of files.", 413);
    }
    catch (InvalidDataException)
    {
        return Error("The upload could not be read. Check the files and try again.");
    }
    catch (DecoderFallbackException)
    {
        return Error("Save the TXT file as UTF-8 or UTF-16 with a byte order mark, then try again.");
    }
    catch (ArgumentException)
    {
        return Error("A file could not be read. Check that it opens normally and try again.");
    }
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
        logger.LogError(exception, "PDF conversion failed.");
        return Error("Something went wrong during conversion. Try again with a smaller file.", 500);
    }
});

app.Run();

static IResult Error(string message, int status = 400) =>
    Results.Json(new { message }, statusCode: status);

static void AddTextPages(PdfDocument document, string content, CancellationToken cancellationToken)
{
    const double margin = 48;
    const double lineHeight = 16;
    var font = new XFont("Arial", 11);
    PdfPage page = document.AddPage();
    page.Size = PdfSharp.PageSize.A4;
    XGraphics graphics = XGraphics.FromPdfPage(page);
    var y = margin + lineHeight;

    try
    {
        foreach (var paragraph in content.Replace("\t", "    ").ReplaceLineEndings("\n").Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = "";
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (line.Length > 0 && graphics.MeasureString(line + element, font).Width > page.Width.Point - 2 * margin)
                {
                    var space = line.LastIndexOf(' ');
                    if (space > 0 && element != " ")
                    {
                        WriteLine(line[..space]);
                        line = line[(space + 1)..];
                    }
                    else
                    {
                        WriteLine(line);
                        line = "";
                        if (element == " ")
                            continue;
                    }
                }
                line += element;
            }
            WriteLine(line);
        }
    }
    finally
    {
        graphics.Dispose();
    }

    void WriteLine(string line)
    {
        if (y > page.Height.Point - margin)
        {
            graphics.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            graphics = XGraphics.FromPdfPage(page);
            y = margin + lineHeight;
        }
        graphics.DrawString(line, font, XBrushes.Black, margin, y);
        y += lineHeight;
    }
}
