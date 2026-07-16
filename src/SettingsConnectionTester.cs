using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UGTLive
{
    public sealed record SettingsConnectionTestResult(bool Success, string Message);

    /// <summary>
    /// End-to-end tests shared by the Settings buttons and the command-line harness.
    /// Tests deliberately use the same provider services and saved configuration as
    /// normal app requests.
    /// </summary>
    public static class SettingsConnectionTester
    {
        public static async Task<SettingsConnectionTestResult> TestTranslationAsync(
            string provider,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string? configurationError = ValidateTranslationConfiguration(provider);
                if (configurationError != null)
                    return Failure(configurationError);

                ErrorPopupManager.ClearLastError();
                TranslationErrorPolicy.Reset();

                const string sourceLanguage = "ja";
                const string targetLanguage = "en";
                string input = JsonSerializer.Serialize(new
                {
                    source_language = sourceLanguage,
                    target_language = targetLanguage,
                    text_blocks = new[]
                    {
                        new
                        {
                            id = "settings_connection_test",
                            text = "接続テストです。",
                            rect = new { x = 0, y = 0, width = 200, height = 40 }
                        }
                    },
                    previous_context = Array.Empty<object>(),
                    game_info = "Settings connection test"
                });

                string prompt = ConfigManager.Instance.GetServicePrompt(provider)
                    .Replace("{SOURCE_LANG}", Logic.GetLanguageName(sourceLanguage))
                    .Replace("{TARGET_LANG}", Logic.GetLanguageName(targetLanguage));

                ITranslationService service = TranslationServiceFactory.CreateService(provider);
                string? response = await service.TranslateAsync(input, prompt, cancellationToken);
                if (string.IsNullOrWhiteSpace(response))
                {
                    string error = ErrorPopupManager.LastErrorMessage;
                    if (string.IsNullOrWhiteSpace(error))
                        error = TranslationErrorPolicy.Reason;
                    if (string.IsNullOrWhiteSpace(error))
                        error = "The provider returned no response.";
                    return Failure(error);
                }

                string displayResponse = ExtractTranslationText(response);
                if (ContainsFailureMarker(displayResponse))
                    return Failure($"The provider returned an error response: {displayResponse}");

                return Success($"The provider replied: {displayResponse}");
            }
            catch (OperationCanceledException)
            {
                return Failure("The connection test timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
        }

        public static async Task<SettingsConnectionTestResult> TestTtsAsync(
            string serviceName,
            string? voiceId = null,
            bool playAudio = false)
        {
            string? audioPath = null;
            try
            {
                string? configurationError = ValidateTtsConfiguration(serviceName);
                if (configurationError != null)
                    return Failure(configurationError);

                voiceId = string.IsNullOrWhiteSpace(voiceId)
                    ? GetConfiguredTtsVoice(serviceName)
                    : voiceId.Trim();

                string sample = GetTtsSample(voiceId);
                ITtsService service = TtsServiceFactory.CreateService(serviceName);
                audioPath = await service.GenerateAudioFileAsync(sample, voiceId);

                if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                    return Failure("The TTS service returned no audio file. Check the API key, service, and voice settings.");

                long bytes = new FileInfo(audioPath).Length;
                if (bytes == 0)
                    return Failure("The TTS service returned an empty audio file.");

                if (playAudio)
                {
                    await AudioPlaybackManager.Instance.PlayAudioFileAsync(audioPath, "settings_connection_test");
                    return Success($"Generated {bytes:N0} bytes of audio and played the response.");
                }

                return Success($"Generated a valid {bytes:N0}-byte audio response.");
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(audioPath))
                {
                    try { File.Delete(audioPath); }
                    catch (Exception ex) { Console.WriteLine($"Could not delete TTS test file: {ex.Message}"); }
                }
            }
        }

        public static async Task<SettingsConnectionTestResult> TestOpenAiRealtimeAsync(
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return Failure("Enter an OpenAI Realtime API key first.");

            try
            {
                bool translationMode = ConfigManager.Instance.IsOpenAITranslationEnabled();
                string endpoint = translationMode
                    ? $"wss://api.openai.com/v1/realtime/translations?model={Uri.EscapeDataString(ConfigManager.Instance.GetOpenAITranslateModel())}"
                    : "wss://api.openai.com/v1/realtime?model=gpt-realtime";

                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey.Trim()}");
                await socket.ConnectAsync(new Uri(endpoint), cancellationToken);

                string reply = await ReceiveTextMessageAsync(socket, cancellationToken);
                socket.Abort();

                if (string.IsNullOrWhiteSpace(reply))
                    return Failure("The WebSocket connected, but the server returned no session reply.");

                using JsonDocument document = JsonDocument.Parse(reply);
                JsonElement root = document.RootElement;
                string type = root.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString() ?? "unknown"
                    : "unknown";

                if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    string error = root.TryGetProperty("error", out JsonElement errorElement)
                        ? errorElement.ToString()
                        : reply;
                    return Failure($"Realtime API error: {error}");
                }

                string model = "";
                if (root.TryGetProperty("session", out JsonElement sessionElement) &&
                    sessionElement.TryGetProperty("model", out JsonElement modelElement))
                {
                    model = modelElement.GetString() ?? "";
                }

                string modelSuffix = string.IsNullOrWhiteSpace(model) ? "" : $" ({model})";
                return Success($"Realtime API replied with {type}{modelSuffix}.");
            }
            catch (OperationCanceledException)
            {
                return Failure("The Realtime API connection timed out or was cancelled.");
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
        }

        public static async Task<SettingsConnectionTestResult> TestGoogleVisionAsync()
        {
            var (success, message) = await GoogleVisionOCRService.Instance.TestApiKeyAsync();
            return new SettingsConnectionTestResult(success, Compact(message));
        }

        private static string? ValidateTranslationConfiguration(string provider)
        {
            ConfigManager config = ConfigManager.Instance;
            return provider switch
            {
                "Gemini" when string.IsNullOrWhiteSpace(config.GetGeminiApiKey()) => "Enter a Gemini API key first.",
                "ChatGPT" when string.IsNullOrWhiteSpace(config.GetChatGptApiKey()) => "Enter an OpenAI API key first.",
                "Anthropic" when string.IsNullOrWhiteSpace(config.GetAnthropicApiKey()) => "Enter an Anthropic API key first.",
                "OpenRouter" when string.IsNullOrWhiteSpace(config.GetOpenRouterApiKey()) => "Enter an OpenRouter API key first.",
                "Google Translate" when config.GetGoogleTranslateUseCloudApi() &&
                    string.IsNullOrWhiteSpace(config.GetGoogleTranslateApiKey()) => "Enter a Google Cloud Translation API key first.",
                "ClaudeCli" when string.IsNullOrWhiteSpace(config.GetClaudeCliCommand()) => "Enter the Anthropic subscription command first.",
                "CodexCli" when string.IsNullOrWhiteSpace(config.GetCodexCliCommand()) => "Enter the OpenAI subscription command first.",
                "GeminiCli" when string.IsNullOrWhiteSpace(config.GetGeminiCliCommand()) => "Enter the Gemini subscription command first.",
                _ => null
            };
        }

        private static string? ValidateTtsConfiguration(string serviceName)
        {
            ConfigManager config = ConfigManager.Instance;
            return serviceName switch
            {
                "ElevenLabs" when string.IsNullOrWhiteSpace(config.GetElevenLabsApiKey()) => "Enter an ElevenLabs API key first.",
                "Google Cloud TTS" when string.IsNullOrWhiteSpace(config.GetGoogleTtsApiKey()) => "Enter a Google Cloud TTS API key first.",
                _ => null
            };
        }

        private static string GetConfiguredTtsVoice(string serviceName)
        {
            ConfigManager config = ConfigManager.Instance;
            return serviceName switch
            {
                "Google Cloud TTS" => config.GetGoogleTtsVoice(),
                "Qwen3-TTS" => config.GetQwen3TtsVoice(),
                "ElevenLabs" when config.GetElevenLabsUseCustomVoiceId() &&
                    !string.IsNullOrWhiteSpace(config.GetElevenLabsCustomVoiceId()) => config.GetElevenLabsCustomVoiceId(),
                _ => config.GetElevenLabsVoice()
            };
        }

        private static string GetTtsSample(string voiceId)
        {
            if (voiceId.StartsWith("ja-", StringComparison.OrdinalIgnoreCase) || voiceId == "ono_anna")
                return "音声接続テストは成功しました。";
            if (voiceId.StartsWith("ko-", StringComparison.OrdinalIgnoreCase) || voiceId == "sohee")
                return "음성 연결 테스트에 성공했습니다.";
            if (voiceId.StartsWith("cmn-", StringComparison.OrdinalIgnoreCase) ||
                voiceId is "vivian" or "serena" or "uncle_fu" or "dylan" or "eric")
                return "语音连接测试成功。";
            return "The text to speech connection test succeeded.";
        }

        private static async Task<string> ReceiveTextMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException($"The server closed the connection: {socket.CloseStatus} {socket.CloseStatusDescription}");
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string ExtractTranslationText(string response)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(response);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("translated_text", out JsonElement translatedText))
                    return JsonValueToString(translatedText);

                if (root.TryGetProperty("translations", out JsonElement translations) &&
                    translations.ValueKind == JsonValueKind.Array &&
                    translations.GetArrayLength() > 0 &&
                    translations[0].TryGetProperty("translated_text", out translatedText))
                {
                    return JsonValueToString(translatedText);
                }
            }
            catch (JsonException)
            {
            }

            return Compact(response);
        }

        private static string JsonValueToString(JsonElement value)
        {
            return Compact(value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.ToString());
        }

        private static bool ContainsFailureMarker(string response)
        {
            return response.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("[API KEY MISSING]", StringComparison.OrdinalIgnoreCase);
        }

        private static SettingsConnectionTestResult Success(string message) =>
            new(true, Compact(message));

        private static SettingsConnectionTestResult Failure(string message) =>
            new(false, Compact(message));

        private static string Compact(string message, int maxLength = 900)
        {
            message = (message ?? "Unknown error").Trim();
            return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "...";
        }
    }
}
