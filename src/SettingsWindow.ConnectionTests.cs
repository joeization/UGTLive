using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace UGTLive
{
    public partial class SettingsWindow
    {
        private async void TranslationTestButton_Click(object sender, RoutedEventArgs e)
        {
            SetTestRunning(translationTestButton, translationTestProgressBar, translationTestResultText,
                "Sending a real translation request...");

            try
            {
                if (translationServiceComboBox.SelectedItem is not ComboBoxItem item)
                {
                    SetTestResult(translationTestResultText,
                        new SettingsConnectionTestResult(false, "Select a translation service first."));
                    return;
                }

                string provider = GetTranslationServiceId(item);
                SyncTranslationSettingsForTest(provider);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3.5));
                SettingsConnectionTestResult result =
                    await SettingsConnectionTester.TestTranslationAsync(provider, timeout.Token);
                SetTestResult(translationTestResultText, result);
            }
            catch (Exception ex)
            {
                SetTestResult(translationTestResultText,
                    new SettingsConnectionTestResult(false, ex.Message));
            }
            finally
            {
                translationTestButton.IsEnabled = true;
                translationTestProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void TtsTestButton_Click(object sender, RoutedEventArgs e)
        {
            SetTestRunning(ttsTestButton, ttsTestProgressBar, ttsTestResultText,
                "Generating a real audio response...");

            try
            {
                if (ttsServiceComboBox.SelectedItem is not ComboBoxItem item)
                {
                    SetTestResult(ttsTestResultText,
                        new SettingsConnectionTestResult(false, "Select a TTS service first."));
                    return;
                }

                string serviceName = item.Content?.ToString() ?? "ElevenLabs";
                string voiceId = SyncTtsSettingsForTest(serviceName);
                SettingsConnectionTestResult result =
                    await SettingsConnectionTester.TestTtsAsync(serviceName, voiceId, playAudio: true);
                SetTestResult(ttsTestResultText, result);
            }
            catch (Exception ex)
            {
                SetTestResult(ttsTestResultText,
                    new SettingsConnectionTestResult(false, ex.Message));
            }
            finally
            {
                ttsTestButton.IsEnabled = true;
                ttsTestProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void OpenAiRealtimeTestButton_Click(object sender, RoutedEventArgs e)
        {
            SetTestRunning(openAiRealtimeTestButton, openAiRealtimeTestProgressBar,
                openAiRealtimeTestResultText, "Opening a real Realtime API session...");

            try
            {
                string apiKey = openAiRealtimeApiKeyPasswordBox.Password.Trim();
                ConfigManager.Instance.SetOpenAiRealtimeApiKey(apiKey);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                SettingsConnectionTestResult result =
                    await SettingsConnectionTester.TestOpenAiRealtimeAsync(apiKey, timeout.Token);
                SetTestResult(openAiRealtimeTestResultText, result);
            }
            catch (Exception ex)
            {
                SetTestResult(openAiRealtimeTestResultText,
                    new SettingsConnectionTestResult(false, ex.Message));
            }
            finally
            {
                openAiRealtimeTestButton.IsEnabled = true;
                openAiRealtimeTestProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void SyncTranslationSettingsForTest(string provider)
        {
            ConfigManager config = ConfigManager.Instance;
            config.SetTranslationService(provider);

            switch (provider)
            {
                case "Gemini":
                    config.SetGeminiApiKey(geminiApiKeyPasswordBox.Password.Trim());
                    SetIfNotBlank(geminiModelComboBox.Text, config.SetGeminiModel);
                    break;
                case "Ollama":
                    SetIfNotBlank(ollamaModelTextBox.Text, config.SetOllamaModel);
                    break;
                case "ChatGPT":
                    config.SetChatGptApiKey(chatGptApiKeyPasswordBox.Password.Trim());
                    if (chatGptModelComboBox.SelectedItem is ComboBoxItem chatModel)
                        SetIfNotBlank(chatModel.Tag?.ToString(), config.SetChatGptModel);
                    break;
                case "llama.cpp":
                    SetIfNotBlank(llamacppModelTextBox.Text, config.SetLlamaCppModel);
                    break;
                case "Google Translate":
                    config.SetGoogleTranslateApiKey(googleTranslateApiKeyPasswordBox.Password.Trim());
                    break;
                case "Anthropic":
                    config.SetAnthropicApiKey(anthropicApiKeyPasswordBox.Password.Trim());
                    if (anthropicModelComboBox.SelectedItem is ComboBoxItem anthropicModel)
                        SetIfNotBlank(anthropicModel.Tag?.ToString(), config.SetAnthropicModel);
                    break;
                case "OpenRouter":
                    config.SetOpenRouterApiKey(openRouterApiKeyPasswordBox.Password.Trim());
                    SetIfNotBlank(openRouterModelComboBox.Text, config.SetOpenRouterModel);
                    break;
                case "ClaudeCli":
                    SetIfNotBlank(claudeCliCommandTextBox.Text, config.SetClaudeCliCommand);
                    SetIfNotBlank(claudeCliModelComboBox.Text, config.SetClaudeCliModel);
                    break;
                case "CodexCli":
                    SetIfNotBlank(codexCliCommandTextBox.Text, config.SetCodexCliCommand);
                    SetIfNotBlank(codexCliModelComboBox.Text, config.SetCodexCliModel);
                    break;
                case "GeminiCli":
                    SetIfNotBlank(geminiCliCommandTextBox.Text, config.SetGeminiCliCommand);
                    SetIfNotBlank(geminiCliModelComboBox.Text, config.SetGeminiCliModel);
                    break;
            }
        }

        private string SyncTtsSettingsForTest(string serviceName)
        {
            ConfigManager config = ConfigManager.Instance;
            config.SetTtsService(serviceName);

            switch (serviceName)
            {
                case "Google Cloud TTS":
                    config.SetGoogleTtsApiKey(googleTtsApiKeyPasswordBox.Password.Trim());
                    string googleVoice = GetSelectedTag(googleTtsVoiceComboBox, config.GetGoogleTtsVoice());
                    config.SetGoogleTtsVoice(googleVoice);
                    return googleVoice;

                case "Qwen3-TTS":
                    string qwenVoice = GetSelectedTag(qwen3TtsVoiceComboBox, config.GetQwen3TtsVoice());
                    config.SetQwen3TtsVoice(qwenVoice);
                    return qwenVoice;

                default:
                    config.SetElevenLabsApiKey(elevenLabsApiKeyPasswordBox.Password.Trim());
                    bool useCustom = elevenLabsCustomVoiceCheckBox.IsChecked == true;
                    string customVoice = elevenLabsCustomVoiceIdTextBox.Text.Trim();
                    config.SetElevenLabsUseCustomVoiceId(useCustom);
                    config.SetElevenLabsCustomVoiceId(customVoice);
                    if (useCustom && !string.IsNullOrWhiteSpace(customVoice))
                        return customVoice;

                    string elevenVoice = GetSelectedTag(elevenLabsVoiceComboBox, config.GetElevenLabsVoice());
                    config.SetElevenLabsVoice(elevenVoice);
                    return elevenVoice;
            }
        }

        private static string GetSelectedTag(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
        }

        private static void SetIfNotBlank(string? value, Action<string> setter)
        {
            value = value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                setter(value);
        }

        private static void SetTestRunning(
            Button button,
            ProgressBar progressBar,
            TextBox resultText,
            string message)
        {
            button.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            resultText.Text = message;
            resultText.ToolTip = null;
            resultText.Foreground = new SolidColorBrush(Colors.Gray);
        }

        private static void SetTestResult(TextBox resultText, SettingsConnectionTestResult result)
        {
            resultText.Text = result.Success ? $"Success: {result.Message}" : $"Error: {result.Message}";
            resultText.ToolTip = result.Message;
            resultText.Foreground = new SolidColorBrush(result.Success ? Colors.Green : Colors.Red);
        }

        private static void ClearTestResult(TextBox? resultText)
        {
            if (resultText == null)
                return;
            resultText.Text = "";
            resultText.ToolTip = null;
        }
    }
}
