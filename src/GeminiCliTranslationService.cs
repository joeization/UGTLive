using System;
using System.Text.Json;

namespace UGTLive
{
    /// <summary>
    /// Translation via the Gemini CLI in headless mode
    /// (`gemini --output-format json`, prompt piped via stdin). Uses the user's
    /// Google account login - no API key.
    /// </summary>
    public class GeminiCliTranslationService : CliTranslationServiceBase
    {
        protected override string ProviderName => "Gemini CLI (Enterprise)";

        protected override string SetupHint =>
            "Gemini CLI subscription login now requires Gemini Code Assist Standard/Enterprise " +
            "or supported Google Cloud access. Personal, free, AI Pro, and AI Ultra accounts " +
            "must use Antigravity, whose headless output cannot yet be captured reliably by UGTLive. " +
            "For an individual account, select the Gemini API provider instead.";

        protected override string GetCommand() => ConfigManager.Instance.GetGeminiCliCommand();

        protected override string BuildArguments(bool thinkingEnabled)
        {
            // --skip-trust: app dir isn't a "trusted folder" (required headless).
            // -e none: load no extensions (big startup win).
            // Gemini CLI exposes no reasoning/thinking flag - model is the only
            // speed lever (thinkingEnabled is a documented no-op here).
            string model = ConfigManager.Instance.GetGeminiCliModel();
            return $"--output-format json --skip-trust -e none -m {model}";
        }

        protected override string ExtractText(string stdout)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("response", out JsonElement response))
                {
                    return response.GetString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not parse Gemini CLI JSON ({ex.Message}); using raw output");
            }
            return stdout.Trim();
        }
    }
}
