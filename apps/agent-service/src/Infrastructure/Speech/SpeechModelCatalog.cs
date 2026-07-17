namespace AgentService.Infrastructure.Speech;

internal static class SpeechModelCatalog
{
    public const string DefaultModelId = "small-q5_1";

    public static IReadOnlyList<SpeechModelDefinition> All { get; } =
    [
        new(
            "small-q5_1",
            "Whisper Small Q5_1",
            "ggml-small-q5_1.bin",
            "推薦。多語模型，適合 Windows CPU-only 電腦，品質與速度較平衡。",
            150_000_000,
            true,
            [
                new(
                    "huggingface",
                    "Hugging Face",
                    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin"),
            ]),
        new(
            "small",
            "Whisper Small",
            "ggml-small.bin",
            "多語完整 small 模型，準確度較穩但 CPU 轉錄會更慢。",
            380_000_000,
            false,
            [
                new(
                    "huggingface",
                    "Hugging Face",
                    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"),
            ]),
        new(
            "base-q5_1",
            "Whisper Base Q5_1",
            "ggml-base-q5_1.bin",
            "速度較快，適合較慢的 CPU，但中文與技術詞彙準確率較低。",
            80_000_000,
            false,
            [
                new(
                    "huggingface",
                    "Hugging Face",
                    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin"),
            ]),
    ];

    public static SpeechModelDefinition Get(string? modelId) =>
        All.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
        ?? All.First(model => model.Id == DefaultModelId);
}
