using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UGTLive
{
    public enum OpenAIAllInOneStage
    {
        Preparing,
        TranslatingAndRendering
    }

    public sealed record OpenAIAllInOneResult(
        byte[] ImageBytes,
        TimeSpan Elapsed,
        string? RequestId,
        Size InputSize,
        Size RequestedOutputSize,
        Size ReceivedOutputSize,
        Size RestoredSize)
    {
        public long InputPixels => (long)InputSize.Width * InputSize.Height;
        public long RequestedOutputPixels => (long)RequestedOutputSize.Width * RequestedOutputSize.Height;
        public long ReceivedOutputPixels => (long)ReceivedOutputSize.Width * ReceivedOutputSize.Height;
        public long RestoredPixels => (long)RestoredSize.Width * RestoredSize.Height;
    }

    public sealed class OpenAIAllInOneException : Exception
    {
        public HttpStatusCode? StatusCode { get; }

        public OpenAIAllInOneException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
        }
    }

    public sealed record OpenAIImagePreparation(
        byte[] PngBytes,
        Size OriginalSize,
        Size InputSize,
        Size RequestedOutputSize,
        Rectangle OutputContentBounds)
    {
        public string ApiSize => $"{RequestedOutputSize.Width}x{RequestedOutputSize.Height}";
        public Size PreparedSize => RequestedOutputSize;
        public Rectangle ContentBounds => OutputContentBounds;
    }

    public static class OpenAIAllInOneImageNormalizer
    {
        public const int MaximumEdge = 3840;
        public const int MinimumPixels = 655_360;
        public const int MaximumPixels = 8_294_400;
        public const double MaximumAspectRatio = 3.0;

        public static OpenAIImagePreparation Prepare(
            Bitmap source,
            int inputMaxEdge = 1024,
            int outputTargetPixels = MinimumPixels)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Width < 1 || source.Height < 1)
                throw new ArgumentException("The captured image has invalid dimensions.", nameof(source));

            int canvasWidth = source.Width;
            int canvasHeight = source.Height;
            if ((double)canvasWidth / canvasHeight > MaximumAspectRatio)
                canvasHeight = (int)Math.Ceiling(canvasWidth / MaximumAspectRatio);
            else if ((double)canvasHeight / canvasWidth > MaximumAspectRatio)
                canvasWidth = (int)Math.Ceiling(canvasHeight / MaximumAspectRatio);

            var paddedCanvasSize = new Size(canvasWidth, canvasHeight);
            Size inputSize = CalculateInputSize(canvasWidth, canvasHeight, inputMaxEdge);
            Size requestedOutputSize = CalculatePreparedSize(canvasWidth, canvasHeight, outputTargetPixels);
            Rectangle inputContentBounds = CalculateContentBounds(source.Size, paddedCanvasSize, inputSize);
            Rectangle outputContentBounds = CalculateContentBounds(source.Size, paddedCanvasSize, requestedOutputSize);

            using var prepared = new Bitmap(inputSize.Width, inputSize.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(prepared))
            {
                graphics.Clear(Color.FromArgb(255, 127, 127, 127));
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, inputContentBounds);
            }

            using var stream = new MemoryStream();
            prepared.Save(stream, ImageFormat.Png);
            return new OpenAIImagePreparation(
                stream.ToArray(),
                source.Size,
                inputSize,
                requestedOutputSize,
                outputContentBounds);
        }

        public static byte[] Restore(byte[] generatedImageBytes, OpenAIImagePreparation preparation)
        {
            ArgumentNullException.ThrowIfNull(generatedImageBytes);
            ArgumentNullException.ThrowIfNull(preparation);

            try
            {
                using var input = new MemoryStream(generatedImageBytes, writable: false);
                using var decoded = new Bitmap(input);
                using var normalized = new Bitmap(preparation.RequestedOutputSize.Width, preparation.RequestedOutputSize.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(normalized))
                {
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(decoded, new Rectangle(Point.Empty, preparation.RequestedOutputSize));
                }

                using var restored = new Bitmap(preparation.OriginalSize.Width, preparation.OriginalSize.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(restored))
                {
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(
                        normalized,
                        new Rectangle(Point.Empty, preparation.OriginalSize),
                        preparation.OutputContentBounds,
                        GraphicsUnit.Pixel);
                }

                using var output = new MemoryStream();
                restored.Save(output, ImageFormat.Png);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException)
            {
                throw new OpenAIAllInOneException("OpenAI returned image data that could not be decoded.", innerException: ex);
            }
        }

        public static Size CalculateInputSize(int width, int height, int maxEdge)
        {
            if (width < 1 || height < 1)
                throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");

            maxEdge = Math.Clamp(maxEdge, 256, MaximumEdge);
            double scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
            int targetWidth = Math.Clamp(RoundToMultiple(width * scale, 16), 16, maxEdge);
            int targetHeight = Math.Clamp(RoundToMultiple(height * scale, 16), 16, maxEdge);

            if ((double)targetWidth / targetHeight > MaximumAspectRatio)
                targetHeight = Math.Min(maxEdge, CeilingToMultiple(targetWidth / MaximumAspectRatio, 16));
            else if ((double)targetHeight / targetWidth > MaximumAspectRatio)
                targetWidth = Math.Min(maxEdge, CeilingToMultiple(targetHeight / MaximumAspectRatio, 16));

            return new Size(targetWidth, targetHeight);
        }

        public static Size CalculatePreparedSize(
            int width,
            int height,
            int targetPixels = MinimumPixels)
        {
            if (width < 1 || height < 1)
                throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");

            targetPixels = Math.Clamp(targetPixels, MinimumPixels, MaximumPixels);
            double scale = Math.Sqrt((double)targetPixels / ((long)width * height));
            scale = Math.Min(scale, (double)MaximumEdge / Math.Max(width, height));

            int targetWidth = CeilingToMultiple(width * scale, 16);
            int targetHeight = CeilingToMultiple(height * scale, 16);
            targetWidth = Math.Clamp(targetWidth, 16, MaximumEdge);
            targetHeight = Math.Clamp(targetHeight, 16, MaximumEdge);

            if ((double)targetWidth / targetHeight > MaximumAspectRatio)
                targetHeight = Math.Min(MaximumEdge, CeilingToMultiple(targetWidth / MaximumAspectRatio, 16));
            else if ((double)targetHeight / targetWidth > MaximumAspectRatio)
                targetWidth = Math.Min(MaximumEdge, CeilingToMultiple(targetHeight / MaximumAspectRatio, 16));

            while ((long)targetWidth * targetHeight < MinimumPixels)
            {
                if (targetWidth >= targetHeight && targetWidth + 16 <= MaximumEdge)
                    targetWidth += 16;
                else if (targetHeight + 16 <= MaximumEdge)
                    targetHeight += 16;
                else
                    break;
            }

            while ((long)targetWidth * targetHeight > MaximumPixels)
            {
                if (targetWidth >= targetHeight && targetWidth > 16)
                    targetWidth -= 16;
                else if (targetHeight > 16)
                    targetHeight -= 16;
                else
                    break;
            }

            return new Size(targetWidth, targetHeight);
        }

        public static Size ReadImageSize(byte[] imageBytes)
        {
            ArgumentNullException.ThrowIfNull(imageBytes);
            try
            {
                using var stream = new MemoryStream(imageBytes, writable: false);
                using var decoded = new Bitmap(stream);
                return decoded.Size;
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException)
            {
                throw new OpenAIAllInOneException("OpenAI returned image data that could not be decoded.", innerException: ex);
            }
        }

        private static Rectangle CalculateContentBounds(Size sourceSize, Size paddedCanvasSize, Size targetSize)
        {
            double scale = Math.Min(
                (double)targetSize.Width / paddedCanvasSize.Width,
                (double)targetSize.Height / paddedCanvasSize.Height);
            int contentWidth = Math.Clamp((int)Math.Round(sourceSize.Width * scale), 1, targetSize.Width);
            int contentHeight = Math.Clamp((int)Math.Round(sourceSize.Height * scale), 1, targetSize.Height);
            return new Rectangle(
                (targetSize.Width - contentWidth) / 2,
                (targetSize.Height - contentHeight) / 2,
                contentWidth,
                contentHeight);
        }

        private static int RoundToMultiple(double value, int multiple)
        {
            return Math.Max(multiple, (int)Math.Round(value / multiple) * multiple);
        }

        private static int CeilingToMultiple(double value, int multiple)
        {
            return Math.Max(multiple, (int)Math.Ceiling(value / multiple) * multiple);
        }
    }

    public sealed class OpenAIAllInOneImageService
    {
        private static readonly Uri ImageEditEndpoint = new("https://api.openai.com/v1/images/edits");
        private static readonly Uri ModelsEndpointBase = new("https://api.openai.com/v1/models/");
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
        private readonly HttpClient _httpClient;

        public const string PromptTemplate = "Edit only the text in this image. Translate all visible {SOURCE_LANGUAGE} text to {TARGET_LANGUAGE} directly in the image. Preserve the original layout, borders, spacing, alignment, typography hierarchy, photos, graphics, colors, and overall visual appearance; keep everything other than the minimum pixels needed to replace text unchanged. Favor literal translation over paraphrase. Preserve reading order, dates, names, brands, quoted titles, and unusual phrasing as much as possible. Preserve numeric values, prices, currency symbols, currency units, measurements, and product quantities exactly; translate unit words only when needed, but do not convert currencies or amounts. Resize translated text as needed to fit the original text regions. Keep translated text inside the original text area and do not overlap decorative rules, borders, icons, photos, hands, or other non-text graphics. Do not add subtitles, annotations, callouts, bounding boxes, JSON, coordinates, side-by-side translations, or text that was not present in the source image. Do not leave untranslated {SOURCE_LANGUAGE} text visible unless it is a proper noun, brand name, or intentionally untranslated title.";

        public OpenAIAllInOneImageService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public static string BuildPrompt(string sourceLanguageName, string targetLanguageName)
        {
            return PromptTemplate
                .Replace("{SOURCE_LANGUAGE}", sourceLanguageName, StringComparison.Ordinal)
                .Replace("{TARGET_LANGUAGE}", targetLanguageName, StringComparison.Ordinal);
        }

        public async Task<OpenAIAllInOneResult> TranslateImageAsync(
            Bitmap source,
            string sourceLanguageName,
            string targetLanguageName,
            string apiKey,
            string model,
            OpenAIAllInOneQuality quality,
            int inputMaxEdge,
            int outputTargetPixels,
            IProgress<OpenAIAllInOneStage>? progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new OpenAIAllInOneException("Enter an OpenAI API key in Settings > OCR & Detection before using OpenAI All In One.");

            var stopwatch = Stopwatch.StartNew();
            progress?.Report(OpenAIAllInOneStage.Preparing);
            OpenAIImagePreparation preparation = OpenAIAllInOneImageNormalizer.Prepare(
                source,
                inputMaxEdge,
                outputTargetPixels);
            progress?.Report(OpenAIAllInOneStage.TranslatingAndRendering);

            using var request = new HttpRequestMessage(HttpMethod.Post, ImageEditEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(BuildPrompt(sourceLanguageName, targetLanguageName)), "prompt");
            form.Add(new StringContent(quality.ToString().ToLowerInvariant()), "quality");
            form.Add(new StringContent(preparation.ApiSize), "size");
            form.Add(new StringContent("png"), "output_format");
            form.Add(new StringContent("1"), "n");
            var imageContent = new ByteArrayContent(preparation.PngBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(imageContent, "image[]", "snapshot.png");
            request.Content = form;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OpenAIAllInOneException("OpenAI image translation timed out after five minutes. Try a smaller capture or lower quality.");
            }
            catch (HttpRequestException ex)
            {
                throw new OpenAIAllInOneException("Could not reach OpenAI. Check the network connection and try again.", innerException: ex);
            }

            using (response)
            {
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw CreateApiException(response.StatusCode, responseBody);

                byte[] generatedBytes = ParseImageBytes(responseBody);
                Size receivedOutputSize = OpenAIAllInOneImageNormalizer.ReadImageSize(generatedBytes);
                byte[] restoredBytes = OpenAIAllInOneImageNormalizer.Restore(generatedBytes, preparation);
                stopwatch.Stop();
                string? requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
                return new OpenAIAllInOneResult(
                    restoredBytes,
                    stopwatch.Elapsed,
                    requestId,
                    preparation.InputSize,
                    preparation.RequestedOutputSize,
                    receivedOutputSize,
                    preparation.OriginalSize);
            }
        }

        public async Task TestAccessAsync(string apiKey, string model, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new OpenAIAllInOneException("Enter an OpenAI API key before testing access.");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ModelsEndpointBase, Uri.EscapeDataString(model)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw CreateApiException(response.StatusCode, body);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OpenAIAllInOneException("The OpenAI access test timed out. Check the network connection and try again.");
            }
            catch (HttpRequestException ex)
            {
                throw new OpenAIAllInOneException("Could not reach OpenAI. Check the network connection and try again.", innerException: ex);
            }
        }

        private static byte[] ParseImageBytes(string responseBody)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                JsonElement data = document.RootElement.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    throw new JsonException("The data array was empty.");
                string? base64 = data[0].TryGetProperty("b64_json", out JsonElement imageElement)
                    ? imageElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(base64))
                    throw new JsonException("The response did not contain b64_json.");
                return Convert.FromBase64String(base64);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                throw new OpenAIAllInOneException("OpenAI returned an unexpected image response. Try again, and enable debug logging if the problem continues.", innerException: ex);
            }
        }

        private static OpenAIAllInOneException CreateApiException(HttpStatusCode statusCode, string responseBody)
        {
            string? detail = TryReadErrorMessage(responseBody);
            string message = statusCode switch
            {
                HttpStatusCode.Unauthorized => "The OpenAI API key was rejected. Check the key in Settings > OCR & Detection.",
                HttpStatusCode.Forbidden => "This OpenAI project cannot use GPT Image. Check model access and complete organization verification if OpenAI requires it.",
                HttpStatusCode.TooManyRequests => "OpenAI rejected the request because of a rate limit or insufficient quota. Check project usage and billing, then try again.",
                HttpStatusCode.BadRequest => "OpenAI rejected the image request. Try a smaller capture or a different quality setting.",
                _ when (int)statusCode >= 500 => "OpenAI's image service is temporarily unavailable. Try again shortly.",
                _ => $"OpenAI image translation failed with HTTP {(int)statusCode}."
            };

            if (!string.IsNullOrWhiteSpace(detail) && statusCode is not HttpStatusCode.Unauthorized)
            {
                string conciseDetail = detail.Length <= 300 ? detail : detail[..300] + "...";
                message += $" OpenAI: {conciseDetail}";
            }
            return new OpenAIAllInOneException(message, statusCode);
        }

        private static string? TryReadErrorMessage(string responseBody)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                    error.TryGetProperty("message", out JsonElement message))
                    return message.GetString();
            }
            catch (JsonException)
            {
            }
            return null;
        }
    }
}
