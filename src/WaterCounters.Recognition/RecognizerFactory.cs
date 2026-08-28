using WaterCounters.Core.Configuration;
using WaterCounters.Recognition.Preprocessing;
using WaterCounters.Recognition.Vlm;

namespace WaterCounters.Recognition;

/// <summary>
/// Сборка распознавателя по настройкам. Одно место, где выбор реализации,
/// предобработки и ансамбля превращается в готовый <see cref="IMeterRecognizer"/>.
/// </summary>
public static class RecognizerFactory
{
    /// <param name="report">Куда рассказать о принятых решениях — в журнал хоста или в консоль бенчмарка.</param>
    public static IMeterRecognizer Create(
        RecognitionSettings settings,
        HttpClient http,
        string? fixturesDirectory = null,
        Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(http);

        IImagePreprocessor preprocessor = CreatePreprocessor(settings, report);

        var preprocess = new PreprocessOptions
        {
            MaxDimension = settings.MaxImageDimension,
            Enhance = settings.Preprocess && settings.EnhanceDarkFrames,
            DetectPanel = settings.Preprocess,
        };

        if (settings.Provider == RecognitionProvider.Stub)
        {
            report?.Invoke($"Распознавание: заглушка по фикстурам из '{fixturesDirectory ?? "(не задано)"}'.");
            return StubRecognizer.FromFixtures(fixturesDirectory ?? "fixtures/meters");
        }

        var options = new VlmRecognizerOptions
        {
            Endpoint = settings.Endpoint,
            Model = settings.Model,
            Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)),
            ContextTokens = settings.ContextTokens,
            Prompt = settings.Prompt,
            SeparateSerialPass = settings.SeparateSerialPass,
            Preprocess = preprocess,
        };

        VlmRecognizer recognizer = settings.Provider == RecognitionProvider.OpenAiCompatible
            ? new OpenAiCompatibleRecognizer(http, options, preprocessor)
            : new OllamaRecognizer(http, options, preprocessor);

        report?.Invoke($"Распознавание: {settings.Provider}, модель {settings.Model} на {settings.Endpoint}.");

        if (settings.EnsemblePasses <= 1)
        {
            return recognizer;
        }

        report?.Invoke($"Ансамбль: {settings.EnsemblePasses} прохода с разными кропами, голосование большинством.");

        return new EnsembleRecognizer(
            recognizer,
            preprocessor,
            preprocess,
            new EnsembleOptions { Passes = settings.EnsemblePasses });
    }

    /// <summary>
    /// Предобработка на OpenCV, если она включена и нативная часть загружается.
    /// Отсутствие DLL роняет распознавание в качестве, но не роняет обработчик:
    /// без выравнивания он ещё работает, а не запустившись — уже нет.
    /// </summary>
    public static IImagePreprocessor CreatePreprocessor(RecognitionSettings settings, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Preprocess)
        {
            report?.Invoke("Предобработка выключена в настройках — кадр уходит модели как есть.");
            return new PassThroughImagePreprocessor();
        }

        if (OpenCvImagePreprocessor.IsAvailable(out string? error))
        {
            return new OpenCvImagePreprocessor();
        }

        report?.Invoke($"OpenCV недоступен ({error}) — предобработка отключена, кадр уходит модели как есть.");
        return new PassThroughImagePreprocessor();
    }
}
