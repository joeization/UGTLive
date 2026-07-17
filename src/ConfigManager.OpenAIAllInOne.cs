using System;

namespace UGTLive
{
    public partial class ConfigManager
    {
        public SnapshotProcessingMode GetSnapshotProcessingMode()
        {
            string value = GetValue(SNAPSHOT_PROCESSING_MODE, SnapshotProcessingMode.Standard.ToString());
            return Enum.TryParse(value, ignoreCase: true, out SnapshotProcessingMode mode)
                ? mode
                : SnapshotProcessingMode.Standard;
        }

        public void SetSnapshotProcessingMode(SnapshotProcessingMode mode)
        {
            _configValues[SNAPSHOT_PROCESSING_MODE] = mode.ToString();
            SaveConfig();
        }

        public string GetOpenAIAllInOneApiKey()
        {
            return GetValue(OPENAI_ALL_IN_ONE_API_KEY, string.Empty);
        }

        public void SetOpenAIAllInOneApiKey(string apiKey)
        {
            _configValues[OPENAI_ALL_IN_ONE_API_KEY] = apiKey?.Trim() ?? string.Empty;
            SaveConfig();
        }

        public string GetOpenAIAllInOneModel()
        {
            return GetValue(OPENAI_ALL_IN_ONE_MODEL, "gpt-image-2");
        }

        public OpenAIAllInOneQuality GetOpenAIAllInOneQuality()
        {
            string value = GetValue(OPENAI_ALL_IN_ONE_QUALITY, "medium");
            return Enum.TryParse(value, ignoreCase: true, out OpenAIAllInOneQuality quality)
                ? quality
                : OpenAIAllInOneQuality.Medium;
        }

        public void SetOpenAIAllInOneQuality(OpenAIAllInOneQuality quality)
        {
            _configValues[OPENAI_ALL_IN_ONE_QUALITY] = quality.ToString().ToLowerInvariant();
            SaveConfig();
        }

        public int GetOpenAIAllInOneInputMaxEdge()
        {
            string value = GetValue(OPENAI_ALL_IN_ONE_INPUT_MAX_EDGE, "1024");
            return int.TryParse(value, out int maxEdge)
                ? Math.Clamp(maxEdge, 256, OpenAIAllInOneImageNormalizer.MaximumEdge)
                : 1024;
        }

        public void SetOpenAIAllInOneInputMaxEdge(int maxEdge)
        {
            _configValues[OPENAI_ALL_IN_ONE_INPUT_MAX_EDGE] = Math.Clamp(
                maxEdge,
                256,
                OpenAIAllInOneImageNormalizer.MaximumEdge).ToString();
            SaveConfig();
        }

        public int GetOpenAIAllInOneOutputTargetPixels()
        {
            string value = GetValue(OPENAI_ALL_IN_ONE_OUTPUT_TARGET_PIXELS, "655360");
            return int.TryParse(value, out int pixels)
                ? Math.Clamp(
                    pixels,
                    OpenAIAllInOneImageNormalizer.MinimumPixels,
                    OpenAIAllInOneImageNormalizer.MaximumPixels)
                : OpenAIAllInOneImageNormalizer.MinimumPixels;
        }

        public void SetOpenAIAllInOneOutputTargetPixels(int pixels)
        {
            _configValues[OPENAI_ALL_IN_ONE_OUTPUT_TARGET_PIXELS] = Math.Clamp(
                pixels,
                OpenAIAllInOneImageNormalizer.MinimumPixels,
                OpenAIAllInOneImageNormalizer.MaximumPixels).ToString();
            SaveConfig();
        }
    }
}
