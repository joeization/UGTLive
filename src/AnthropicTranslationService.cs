using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UGTLive
{
    /// <summary>
    /// Translation via Anthropic's native Messages API (https://api.anthropic.com/v1/messages).
    /// Uses x-api-key auth and maps the shared "Enable Thinking Mode" checkbox to the
    /// selected model's manual or adaptive-thinking request format.
    /// </summary>
    public class AnthropicTranslationService : ITranslationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string ApiEndpoint = "https://api.anthropic.com/v1/messages";

        private static bool IsFableOrMythos5(string model)
        {
            return model.StartsWith("claude-fable-5", StringComparison.OrdinalIgnoreCase) ||
                   model.StartsWith("claude-mythos-5", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSonnet5(string model)
        {
            return model.StartsWith("claude-sonnet-5", StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesOptInAdaptiveThinking(string model)
        {
            return model.StartsWith("claude-opus-4-6", StringComparison.OrdinalIgnoreCase) ||
                   model.StartsWith("claude-opus-4-7", StringComparison.OrdinalIgnoreCase) ||
                   model.StartsWith("claude-opus-4-8", StringComparison.OrdinalIgnoreCase) ||
                   model.StartsWith("claude-sonnet-4-6", StringComparison.OrdinalIgnoreCase);
        }

        private static void ConfigureThinking(
            Dictionary<string, object> requestBody,
            string model,
            bool thinkingEnabled,
            int maxTokens)
        {
            // Fable/Mythos 5 always use adaptive thinking and reject manual or disabled modes.
            // Use effort to make the shared checkbox select the high-quality or low-latency path.
            if (IsFableOrMythos5(model))
            {
                requestBody["output_config"] = new Dictionary<string, string>
                {
                    { "effort", thinkingEnabled ? "high" : "low" }
                };
                return;
            }

            // Sonnet 5 uses adaptive thinking by default and explicitly supports disabling it.
            if (IsSonnet5(model))
            {
                if (!thinkingEnabled)
                {
                    requestBody["thinking"] = new Dictionary<string, string>
                    {
                        { "type", "disabled" }
                    };
                }
                return;
            }

            // Newer Opus models and Sonnet 4.6 reject or deprecate fixed token budgets.
            if (UsesOptInAdaptiveThinking(model))
            {
                if (thinkingEnabled)
                {
                    requestBody["thinking"] = new Dictionary<string, string>
                    {
                        { "type", "adaptive" }
                    };
                    requestBody["output_config"] = new Dictionary<string, string>
                    {
                        { "effort", "high" }
                    };
                }
                return;
            }

            if (!thinkingEnabled)
            {
                return;
            }

            const int budgetTokens = 4096;
            // Older models with manual thinking require max_tokens > budget_tokens.
            if (maxTokens <= budgetTokens)
            {
                requestBody["max_tokens"] = budgetTokens + 4096;
            }
            requestBody["thinking"] = new Dictionary<string, object>
            {
                { "type", "enabled" },
                { "budget_tokens", budgetTokens }
            };
        }

        public async Task<string?> TranslateAsync(string jsonData, string prompt, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                string apiKey = ConfigManager.Instance.GetAnthropicApiKey();
                string model = ConfigManager.Instance.GetAnthropicModel();

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Console.WriteLine("Anthropic API key is missing. Please set it in the settings.");
                    ErrorPopupManager.ShowError(
                        "Anthropic API key is missing.\n\nPlease set it in Settings > Translation.",
                        "Anthropic Translation Error");
                    return null;
                }

                JsonElement inputJson = JsonSerializer.Deserialize<JsonElement>(jsonData);

                int maxTokens = ConfigManager.Instance.GetAnthropicMaxTokens();
                bool thinkingEnabled = ConfigManager.Instance.GetThinkingEnabled();

                var messages = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string>
                    {
                        { "role", "user" },
                        { "content", "Here is the input JSON:\n\n" + jsonData }
                    }
                };

                var requestBody = new Dictionary<string, object>
                {
                    { "model", model },
                    { "max_tokens", maxTokens },
                    { "system", prompt },
                    { "messages", messages }
                };

                ConfigureThinking(requestBody, model, thinkingEnabled, maxTokens);

                string requestJson = JsonSerializer.Serialize(requestBody);

                LogManager.Instance.LogLlmRequest(prompt, jsonData, "POST", ApiEndpoint, requestJson);

                Console.WriteLine($"Sending request to Anthropic API ({model})");
                var stopwatch = Stopwatch.StartNew();

                var response = await RetryHelper.SendWithRetryAsync(
                    ct =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
                        request.Headers.Add("x-api-key", apiKey);
                        request.Headers.Add("anthropic-version", "2023-06-01");
                        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                        return _httpClient.SendAsync(request, ct);
                    },
                    cancellationToken,
                    maxRetries: 3,
                    baseDelayMs: 10000,
                    onRetry: (attempt, status) => Console.WriteLine($"[Anthropic] Retry {attempt} after HTTP {(int)status}"));

                string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Anthropic response complete: {stopwatch.Elapsed.TotalSeconds:F1}s, {responseContent.Length} chars");
                    LogManager.Instance.LogLlmReply(responseContent);

                    if (TryGetRefusalMessage(responseContent, out string refusalMessage))
                    {
                        Console.WriteLine($"Anthropic request refused: {refusalMessage}");
                        ErrorPopupManager.ShowError(
                            refusalMessage + "\n\nTry Claude Opus or Sonnet for this translation.",
                            "Anthropic Request Refused");
                        return null;
                    }

                    string translatedText = ParseResponse(responseContent);
                    if (string.IsNullOrEmpty(translatedText))
                    {
                        Console.WriteLine("Failed to extract translated text from Anthropic response");
                        return null;
                    }

                    return TranslationResponseFormatter.Format(translatedText, jsonData, inputJson);
                }

                Console.WriteLine($"Error calling Anthropic API: {response.StatusCode}");
                Console.WriteLine($"Response: {responseContent}");

                if (TranslationErrorPolicy.IsNonRetryableStatus(response.StatusCode))
                    TranslationErrorPolicy.SignalNonRetryable($"Anthropic HTTP {(int)response.StatusCode} ({response.StatusCode})");

                string detailedMessage = $"The Anthropic API returned an error.\n\nStatus: {response.StatusCode}";
                try
                {
                    if (!string.IsNullOrWhiteSpace(responseContent))
                    {
                        using JsonDocument errorDoc = JsonDocument.Parse(responseContent);
                        if (errorDoc.RootElement.TryGetProperty("error", out JsonElement errorElement) &&
                            errorElement.TryGetProperty("message", out JsonElement messageElement))
                        {
                            detailedMessage += $"\n\nError: {messageElement.GetString()}";
                        }
                        else
                        {
                            detailedMessage += $"\n\nResponse: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}";
                        }
                    }
                }
                catch (JsonException)
                {
                    detailedMessage += $"\n\nResponse: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}";
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    detailedMessage += "\n\nPlease check your API key in settings.";
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    detailedMessage += "\n\nRate limited. Please try again later.";
                }

                ErrorPopupManager.ShowError(detailedMessage, "Anthropic Translation Error");
                return null;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Anthropic translation was cancelled");
                return null;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Error connecting to Anthropic API: {ex.Message}");
                ErrorPopupManager.ShowError(
                    $"Failed to connect to Anthropic API.\n\nError: {ex.Message}\n\nPlease check:\n" +
                    "1. Your internet connection\n" +
                    "2. Your API key is correct in settings\n" +
                    "3. Your firewall/antivirus isn't blocking the connection",
                    "Anthropic Connection Error");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in AnthropicTranslationService.TranslateAsync: {ex.Message}");
                ErrorPopupManager.ShowError(
                    $"An unexpected error occurred with Anthropic translation.\n\nError: {ex.Message}",
                    "Anthropic Translation Error");
                return null;
            }
        }

        /// <summary>
        /// Claude Fable 5 reports safety refusals as successful HTTP 200 responses.
        /// </summary>
        private static bool TryGetRefusalMessage(string responseContent, out string message)
        {
            message = "Claude declined this request.";

            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseContent);
                if (!doc.RootElement.TryGetProperty("stop_reason", out JsonElement stopReason) ||
                    !string.Equals(stopReason.GetString(), "refusal", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (doc.RootElement.TryGetProperty("stop_details", out JsonElement stopDetails) &&
                    stopDetails.ValueKind == JsonValueKind.Object &&
                    stopDetails.TryGetProperty("explanation", out JsonElement explanation) &&
                    !string.IsNullOrWhiteSpace(explanation.GetString()))
                {
                    message = explanation.GetString()!;
                }

                return true;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Error parsing Anthropic stop reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Parse the Messages API response. content is a block array; return the
        /// first "text" block, skipping any "thinking" blocks.
        /// </summary>
        private string ParseResponse(string responseContent)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseContent);
                if (doc.RootElement.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out JsonElement type) &&
                            type.GetString() == "text" &&
                            block.TryGetProperty("text", out JsonElement text))
                        {
                            return text.GetString() ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing Anthropic response: {ex.Message}");
            }
            return "";
        }
    }
}
