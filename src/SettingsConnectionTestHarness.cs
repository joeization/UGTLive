using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UGTLive
{
    /// <summary>
    /// Headless entry point for the same end-to-end tests used by Settings buttons.
    /// </summary>
    public static class SettingsConnectionTestHarness
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        public static bool IsTestModeRequested(string[] args) =>
            Array.Exists(args, argument => argument == "--test-settings-connection");

        public static int Run(string[] args)
        {
            AllocConsole();
            ErrorPopupManager.SuppressPopups = true;

            // ConfigManager normally emits a large diagnostic dump while loading.
            // Initialize it before teeing output so the result file stays focused on
            // the connection test (secret-like values are redacted there as well).
            TextWriter consoleWriter = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                _ = ConfigManager.Instance;
            }
            finally
            {
                Console.SetOut(consoleWriter);
            }

            string resultPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "settings_connection_test_result.txt");
            TextWriter? fileWriter = null;
            try { fileWriter = new StreamWriter(resultPath, false) { AutoFlush = true }; }
            catch { }

            if (fileWriter != null)
                Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));

            try
            {
                return Task.Run(() => RunAsync(args)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RESULT: FAIL - {ex}");
                return 1;
            }
            finally
            {
                fileWriter?.Flush();
                fileWriter?.Dispose();
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            string mode = GetArg(args, "--test-settings-connection")?.ToLowerInvariant() ?? "";
            SettingsConnectionTestResult result;

            switch (mode)
            {
                case "translation":
                    result = await RunTranslationAsync(args);
                    break;
                case "tts":
                    result = await RunTtsAsync(args);
                    break;
                case "realtime":
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                    {
                        result = await SettingsConnectionTester.TestOpenAiRealtimeAsync(
                            ConfigManager.Instance.GetOpenAiRealtimeApiKey(), timeout.Token);
                    }
                    break;
                case "vision":
                    result = await SettingsConnectionTester.TestGoogleVisionAsync();
                    break;
                default:
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  ugtlive.exe --test-settings-connection translation [--provider NAME] [--model ID]");
                    Console.WriteLine("  ugtlive.exe --test-settings-connection tts [--service NAME] [--voice ID] [--play-audio]");
                    Console.WriteLine("  ugtlive.exe --test-settings-connection realtime");
                    Console.WriteLine("  ugtlive.exe --test-settings-connection vision");
                    return 2;
            }

            Console.WriteLine(result.Success
                ? $"RESULT: PASS - {result.Message}"
                : $"RESULT: FAIL - {result.Message}");
            return result.Success ? 0 : 1;
        }

        private static async Task<SettingsConnectionTestResult> RunTranslationAsync(string[] args)
        {
            ConfigManager config = ConfigManager.Instance;
            string originalProvider = config.GetCurrentTranslationService();
            string provider = NormalizeProvider(GetArg(args, "--provider") ?? originalProvider);
            string? model = GetArg(args, "--model");
            string? originalModel = null;

            try
            {
                config.SetTranslationService(provider);
                if (!string.IsNullOrWhiteSpace(model))
                    originalModel = ApplyTranslationModel(config, provider, model);

                Console.WriteLine($"Testing Settings translation button path: provider={provider}, model={model ?? "(saved setting)"}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3.5));
                return await SettingsConnectionTester.TestTranslationAsync(provider, timeout.Token);
            }
            finally
            {
                if (originalModel != null)
                    ApplyTranslationModel(config, provider, originalModel);
                config.SetTranslationService(originalProvider);
            }
        }

        private static async Task<SettingsConnectionTestResult> RunTtsAsync(string[] args)
        {
            ConfigManager config = ConfigManager.Instance;
            string originalService = config.GetTtsService();
            string service = GetArg(args, "--service") ?? originalService;
            string? voice = GetArg(args, "--voice");
            bool playAudio = Array.Exists(args, argument => argument == "--play-audio");

            try
            {
                config.SetTtsService(service);
                Console.WriteLine($"Testing Settings TTS button path: service={service}, voice={voice ?? "(saved setting)"}, playback={playAudio}");
                return await SettingsConnectionTester.TestTtsAsync(service, voice, playAudio);
            }
            finally
            {
                config.SetTtsService(originalService);
            }
        }

        private static string? ApplyTranslationModel(ConfigManager config, string provider, string model)
        {
            switch (provider)
            {
                case "Gemini":
                    string gemini = config.GetGeminiModel(); config.SetGeminiModel(model); return gemini;
                case "Ollama":
                    string ollama = config.GetOllamaModel(); config.SetOllamaModel(model); return ollama;
                case "ChatGPT":
                    string chatGpt = config.GetChatGptModel(); config.SetChatGptModel(model); return chatGpt;
                case "llama.cpp":
                    string llama = config.GetLlamaCppModel(); config.SetLlamaCppModel(model); return llama;
                case "Anthropic":
                    string anthropic = config.GetAnthropicModel(); config.SetAnthropicModel(model); return anthropic;
                case "OpenRouter":
                    string openRouter = config.GetOpenRouterModel(); config.SetOpenRouterModel(model); return openRouter;
                case "ClaudeCli":
                    string claude = config.GetClaudeCliModel(); config.SetClaudeCliModel(model); return claude;
                case "CodexCli":
                    string codex = config.GetCodexCliModel(); config.SetCodexCliModel(model); return codex;
                case "GeminiCli":
                    string geminiCli = config.GetGeminiCliModel(); config.SetGeminiCliModel(model); return geminiCli;
                default:
                    return null;
            }
        }

        private static string NormalizeProvider(string provider) => provider switch
        {
            "Anthropic Sub" => "ClaudeCli",
            "OpenAI Sub" => "CodexCli",
            "Gemini Sub" => "GeminiCli",
            _ => provider
        };

        private static string? GetArg(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _first;
            private readonly TextWriter _second;

            public TeeTextWriter(TextWriter first, TextWriter second)
            {
                _first = first;
                _second = second;
            }

            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) { _first.Write(value); _second.Write(value); }
            public override void Write(string? value) { _first.Write(value); _second.Write(value); }
            public override void WriteLine(string? value) { _first.WriteLine(value); _second.WriteLine(value); }
        }
    }
}
