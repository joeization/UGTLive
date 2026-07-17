using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UGTLive
{
    public static class OpenAIAllInOneTestHarness
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        public static bool IsTestModeRequested(string[] args)
        {
            return Array.Exists(args, value => value == "--test-openai-all-in-one");
        }

        public static int Run(string[] args)
        {
            AllocConsole();
            ErrorPopupManager.SuppressPopups = true;
            try
            {
                return Task.Run(() => RunAsync(args)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RESULT: FAIL - {ex}");
                return 1;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            if (Array.Exists(args, value => value == "--contract-only"))
                return await RunContractTestsAsync();

            string? imagePath = GetArg(args, "--image");
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                Console.WriteLine("RESULT: FAIL - provide an existing --image <path>.");
                return 1;
            }

            string sourceCode = GetArg(args, "--source") ?? "ja";
            string targetCode = GetArg(args, "--target") ?? "en";
            string outputPath = GetArg(args, "--output") ?? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(imagePath))!,
                $"{Path.GetFileNameWithoutExtension(imagePath)}.openai-all-in-one.png");
            string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? ConfigManager.Instance.GetOpenAIAllInOneApiKey();
            int inputMaxEdge = GetIntArg(args, "--input-max-edge")
                ?? ConfigManager.Instance.GetOpenAIAllInOneInputMaxEdge();
            int outputTargetPixels = GetIntArg(args, "--output-target-pixels")
                ?? ConfigManager.Instance.GetOpenAIAllInOneOutputTargetPixels();

            using var loaded = new Bitmap(imagePath);
            using var source = new Bitmap(loaded);
            var progress = new Progress<OpenAIAllInOneStage>(stage => Console.WriteLine($"Stage: {stage}"));
            var service = new OpenAIAllInOneImageService();
            OpenAIAllInOneResult result = await service.TranslateImageAsync(
                source,
                Logic.GetLanguageName(sourceCode),
                Logic.GetLanguageName(targetCode),
                apiKey,
                ConfigManager.Instance.GetOpenAIAllInOneModel(),
                ConfigManager.Instance.GetOpenAIAllInOneQuality(),
                inputMaxEdge,
                outputTargetPixels,
                progress,
                CancellationToken.None);

            File.WriteAllBytes(outputPath, result.ImageBytes);
            Console.WriteLine($"Output: {outputPath}");
            Console.WriteLine($"Elapsed: {result.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Uploaded input: {FormatSize(result.InputSize)}");
            Console.WriteLine($"Requested output: {FormatSize(result.RequestedOutputSize)}");
            Console.WriteLine($"API returned: {FormatSize(result.ReceivedOutputSize)}");
            Console.WriteLine($"Restored output: {FormatSize(result.RestoredSize)}");
            Console.WriteLine("RESULT: PASS - translated image produced");
            return 0;
        }

        private static async Task<int> RunContractTestsAsync()
        {
            var failures = new List<string>();
            var cases = new (int width, int height)[]
            {
                (64, 64), (1024, 768), (1919, 1079), (3840, 2160),
                (5000, 4000), (2400, 300), (300, 2400), (1024, 1536)
            };

            foreach ((int width, int height) in cases)
            {
                int paddedWidth = width;
                int paddedHeight = height;
                if ((double)paddedWidth / paddedHeight > OpenAIAllInOneImageNormalizer.MaximumAspectRatio)
                    paddedHeight = (int)Math.Ceiling(paddedWidth / OpenAIAllInOneImageNormalizer.MaximumAspectRatio);
                else if ((double)paddedHeight / paddedWidth > OpenAIAllInOneImageNormalizer.MaximumAspectRatio)
                    paddedWidth = (int)Math.Ceiling(paddedHeight / OpenAIAllInOneImageNormalizer.MaximumAspectRatio);

                foreach (int maxEdge in new[] { 256, 512, 1024, 3840 })
                {
                    Size inputSize = OpenAIAllInOneImageNormalizer.CalculateInputSize(paddedWidth, paddedHeight, maxEdge);
                    if (inputSize.Width % 16 != 0 || inputSize.Height % 16 != 0 ||
                        inputSize.Width > maxEdge || inputSize.Height > maxEdge ||
                        Math.Max((double)inputSize.Width / inputSize.Height, (double)inputSize.Height / inputSize.Width) > 3.0)
                    {
                        failures.Add($"Invalid input size for {width}x{height} at max edge {maxEdge}: {inputSize.Width}x{inputSize.Height}");
                    }
                }

                foreach (int targetPixels in new[]
                {
                    OpenAIAllInOneImageNormalizer.MinimumPixels,
                    1_048_576,
                    2_073_600,
                    OpenAIAllInOneImageNormalizer.MaximumPixels
                })
                {
                    Size size = OpenAIAllInOneImageNormalizer.CalculatePreparedSize(paddedWidth, paddedHeight, targetPixels);
                    long pixels = (long)size.Width * size.Height;
                    if (size.Width % 16 != 0 || size.Height % 16 != 0 ||
                        size.Width > 3840 || size.Height > 3840 ||
                        pixels < OpenAIAllInOneImageNormalizer.MinimumPixels ||
                        pixels > OpenAIAllInOneImageNormalizer.MaximumPixels ||
                        Math.Max((double)size.Width / size.Height, (double)size.Height / size.Width) > 3.0)
                    {
                        failures.Add($"Invalid output size for {width}x{height} at {targetPixels:N0} pixels: {size.Width}x{size.Height}");
                    }
                }
            }

            string prompt = OpenAIAllInOneImageService.BuildPrompt("Japanese", "English");
            if (prompt.Contains("{SOURCE_LANGUAGE}") || prompt.Contains("{TARGET_LANGUAGE}") ||
                !prompt.Contains("Japanese") || !prompt.Contains("English"))
                failures.Add("Prompt substitution failed.");

            using var sample = new Bitmap(320, 180);
            using (Graphics graphics = Graphics.FromImage(sample))
            {
                graphics.Clear(Color.Navy);
                graphics.DrawString("test", SystemFonts.DefaultFont, Brushes.White, new PointF(10, 10));
            }
            const int contractInputMaxEdge = 256;
            const int contractOutputPixels = OpenAIAllInOneImageNormalizer.MinimumPixels;
            OpenAIImagePreparation prepared = OpenAIAllInOneImageNormalizer.Prepare(
                sample,
                contractInputMaxEdge,
                contractOutputPixels);
            byte[] generatedResponse = CreateSolidPng(prepared.RequestedOutputSize);
            var successHandler = new ContractHandler(
                generatedResponse,
                HttpStatusCode.OK,
                prepared.ApiSize);
            var service = new OpenAIAllInOneImageService(new HttpClient(successHandler));
            OpenAIAllInOneResult result = await service.TranslateImageAsync(
                sample, "Japanese", "English", "contract-secret", "gpt-image-2",
                OpenAIAllInOneQuality.Medium, contractInputMaxEdge, contractOutputPixels,
                null, CancellationToken.None);
            if (result.RequestId != "contract-request")
                failures.Add("Response request ID metadata was not preserved.");
            if (result.InputSize != prepared.InputSize ||
                result.RequestedOutputSize != prepared.RequestedOutputSize ||
                result.ReceivedOutputSize != prepared.RequestedOutputSize ||
                result.RestoredSize != sample.Size)
            {
                failures.Add("Input, requested, received, or restored dimension metadata was incorrect.");
            }
            using (var outputStream = new MemoryStream(result.ImageBytes, writable: false))
            using (var output = new Bitmap(outputStream))
            {
                if (output.Width != sample.Width || output.Height != sample.Height)
                    failures.Add($"Restored output was {output.Width}x{output.Height}, expected {sample.Width}x{sample.Height}.");
            }
            failures.AddRange(successHandler.Failures);

            using (var panorama = new Bitmap(600, 100))
            {
                OpenAIImagePreparation panoramaPreparation = OpenAIAllInOneImageNormalizer.Prepare(panorama);
                if (panoramaPreparation.ContentBounds.Height >= panoramaPreparation.PreparedSize.Height)
                    failures.Add("Greater-than-3:1 panorama was not padded.");
                byte[] panoramaRestored = OpenAIAllInOneImageNormalizer.Restore(
                    panoramaPreparation.PngBytes,
                    panoramaPreparation);
                using var panoramaStream = new MemoryStream(panoramaRestored, writable: false);
                using var panoramaOutput = new Bitmap(panoramaStream);
                if (panoramaOutput.Width != panorama.Width || panoramaOutput.Height != panorama.Height)
                    failures.Add("Padded panorama was not restored to its original dimensions.");
            }

            try
            {
                var unauthorized = new OpenAIAllInOneImageService(new HttpClient(new ContractHandler(Array.Empty<byte>(), HttpStatusCode.Unauthorized)));
                await unauthorized.TranslateImageAsync(sample, "Japanese", "English", "bad", "gpt-image-2", OpenAIAllInOneQuality.Medium, 1024, contractOutputPixels, null, CancellationToken.None);
                failures.Add("Unauthorized response did not throw.");
            }
            catch (OpenAIAllInOneException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
            }

            foreach ((HttpStatusCode statusCode, string expectedText) in new[]
            {
                (HttpStatusCode.Forbidden, "cannot use GPT Image"),
                (HttpStatusCode.TooManyRequests, "rate limit or insufficient quota")
            })
            {
                try
                {
                    var failing = new OpenAIAllInOneImageService(new HttpClient(new ContractHandler(Array.Empty<byte>(), statusCode)));
                    await failing.TranslateImageAsync(sample, "Japanese", "English", "contract-secret", "gpt-image-2", OpenAIAllInOneQuality.Medium, 1024, contractOutputPixels, null, CancellationToken.None);
                    failures.Add($"{statusCode} response did not throw.");
                }
                catch (OpenAIAllInOneException ex) when (
                    ex.StatusCode == statusCode && ex.Message.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
                {
                }
            }

            try
            {
                var malformed = new OpenAIAllInOneImageService(new HttpClient(new RawResponseHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[{\"b64_json\":\"not-base64\"}]}")));
                await malformed.TranslateImageAsync(sample, "Japanese", "English", "contract-secret", "gpt-image-2", OpenAIAllInOneQuality.Medium, 1024, contractOutputPixels, null, CancellationToken.None);
                failures.Add("Malformed base64 response did not throw.");
            }
            catch (OpenAIAllInOneException ex) when (ex.Message.Contains("unexpected image response", StringComparison.OrdinalIgnoreCase))
            {
            }

            try
            {
                await service.TranslateImageAsync(sample, "Japanese", "English", string.Empty, "gpt-image-2", OpenAIAllInOneQuality.Medium, 1024, contractOutputPixels, null, CancellationToken.None);
                failures.Add("Missing API key did not fail before sending a request.");
            }
            catch (OpenAIAllInOneException ex) when (ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
            {
            }

            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                try
                {
                    var cancellable = new OpenAIAllInOneImageService(new HttpClient(new CancellationHandler()));
                    await cancellable.TranslateImageAsync(
                        sample, "Japanese", "English", "contract-secret", "gpt-image-2",
                        OpenAIAllInOneQuality.Low, 1024, contractOutputPixels, null, cancellation.Token);
                    failures.Add("Cancellation did not stop the image request.");
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (failures.Count == 0)
            {
                Console.WriteLine("RESULT: PASS - OpenAI All In One offline contract tests passed");
                return 0;
            }

            foreach (string failure in failures)
                Console.WriteLine($"FAIL: {failure}");
            Console.WriteLine($"RESULT: FAIL - {failures.Count} contract test(s) failed");
            return 1;
        }

        private static string? GetArg(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static int? GetIntArg(string[] args, string name)
        {
            string? value = GetArg(args, name);
            return int.TryParse(value, out int parsed) ? parsed : null;
        }

        private static string FormatSize(Size size)
        {
            return $"{size.Width}x{size.Height} = {(long)size.Width * size.Height:N0} pixels";
        }

        private static byte[] CreateSolidPng(Size size)
        {
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.Clear(Color.DarkSlateBlue);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        private sealed class ContractHandler : HttpMessageHandler
        {
            private readonly byte[] _responseImage;
            private readonly HttpStatusCode _statusCode;
            private readonly string? _expectedSize;
            public List<string> Failures { get; } = new();

            public ContractHandler(byte[] responseImage, HttpStatusCode statusCode, string? expectedSize = null)
            {
                _responseImage = responseImage;
                _statusCode = statusCode;
                _expectedSize = expectedSize;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/images/edits")
                    Failures.Add("Unexpected endpoint or HTTP method.");
                if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != "contract-secret")
                    Failures.Add("Authorization header was missing or incorrect.");

                string multipart = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                string normalizedMultipart = multipart.Replace("\"", string.Empty, StringComparison.Ordinal);
                foreach (string expected in new[] { "name=model", "gpt-image-2", "name=image[]", "name=quality", "medium", "name=size", "name=output_format", "png", "Japanese", "English" })
                {
                    if (!normalizedMultipart.Contains(expected, StringComparison.OrdinalIgnoreCase))
                        Failures.Add($"Multipart request did not contain '{expected}'.");
                }
                if (_expectedSize != null && !normalizedMultipart.Contains(_expectedSize, StringComparison.Ordinal))
                    Failures.Add($"Multipart request did not contain requested size '{_expectedSize}'.");

                string body = _statusCode == HttpStatusCode.OK
                    ? JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(_responseImage) } } })
                    : "{\"error\":{\"message\":\"test error\"}}";
                var response = new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                response.Headers.TryAddWithoutValidation("x-request-id", "contract-request");
                return response;
            }
        }

        private sealed class CancellationHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The canceled request unexpectedly continued.");
            }
        }

        private sealed class RawResponseHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _body;

            public RawResponseHandler(HttpStatusCode statusCode, string body)
            {
                _statusCode = statusCode;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
