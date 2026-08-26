using WaterCounters.Core.Metering;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition.Tests;

public class EnsembleRecognizerTests
{
    private static readonly MeterSpec Meter = RecognitionTestData.ColdWater;

    [Fact]
    public async Task MajorityWins()
    {
        RecognitionResult result = await VoteAsync(
            Read(123.456m, 0.90),
            Read(123.456m, 0.88),
            Read(129.456m, 0.95));

        Assert.Equal(123.456m, result.Value);
    }

    [Fact]
    public async Task DisagreementDragsConfidenceBelowTheValidatorThreshold()
    {
        RecognitionResult result = await VoteAsync(
            Read(123.456m, 0.99),
            Read(456.789m, 0.98),
            Read(999.111m, 0.97));

        // Ни одно значение не повторилось: даже при отличной уверенности каждого прохода
        // ансамбль обязан уйти ниже порога 0.80 и попасть человеку на проверку.
        Assert.True(result.Confidence < 0.80, $"уверенность {result.Confidence} должна быть ниже порога");
        Assert.Contains(result.Warnings, w => w.Contains("разошлись в целой части", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullAgreementKeepsConfidenceIntact()
    {
        RecognitionResult result = await VoteAsync(
            Read(123.456m, 0.90),
            Read(123.456m, 0.90),
            Read(123.456m, 0.90));

        Assert.Equal(123.456m, result.Value);
        Assert.Equal(0.90, result.Confidence, 3);
        Assert.Contains(result.Warnings, w => w.Contains("3 из 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FractionalDisagreementKeepsTheIntegerPartAndSaysSo()
    {
        RecognitionResult result = await VoteAsync(
            Read(123.456m, 0.90),
            Read(123.457m, 0.95),
            Read(123.451m, 0.80));

        Assert.Equal(123m, decimal.Truncate(result.Value!.Value));
        Assert.Contains(result.Warnings, w => w.Contains("разошлись в дробной части", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneFailedPassDoesNotSinkTheEnsemble()
    {
        var inner = new ScriptedRecognizer(
            [Read(123.456m, 0.90), null, Read(123.456m, 0.92)],
            failOnPass: 2);

        RecognitionResult result = await Ensemble(inner, passes: 3)
            .RecognizeAsync(Meter, RecognitionTestData.OpaqueJpeg, CancellationToken.None);

        Assert.Equal(123.456m, result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("проход 2", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("прочитали показание 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllPassesFailingIsAnError()
    {
        var inner = new ScriptedRecognizer([null, null, null], failOnPass: 0);

        RecognitionException error = await Assert.ThrowsAsync<RecognitionException>(() =>
            Ensemble(inner, passes: 3).RecognizeAsync(Meter, RecognitionTestData.OpaqueJpeg, CancellationToken.None));

        Assert.Contains("Все проходы ансамбля провалились", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachPassGetsItsOwnCrop()
    {
        var preprocessor = new RecordingPreprocessor();
        var inner = new ScriptedRecognizer([Read(1m, 0.9), Read(1m, 0.9), Read(1m, 0.9)]);

        var ensemble = new EnsembleRecognizer(
            inner,
            preprocessor,
            new PreprocessOptions(),
            new EnsembleOptions { Passes = 3, CropScales = [1.0, 0.85, 1.2] });

        await ensemble.RecognizeAsync(Meter, RecognitionTestData.OpaqueJpeg, CancellationToken.None);

        Assert.Equal(new[] { 1.0, 0.85, 1.2 }, preprocessor.Scales);
    }

    private static async Task<RecognitionResult> VoteAsync(params RecognitionResult[] passes)
    {
        var inner = new ScriptedRecognizer(passes);

        return await Ensemble(inner, passes.Length)
            .RecognizeAsync(Meter, RecognitionTestData.OpaqueJpeg, CancellationToken.None);
    }

    private static EnsembleRecognizer Ensemble(IVariantRecognizer inner, int passes) => new(
        inner,
        new PassThroughImagePreprocessor(),
        new PreprocessOptions(),
        new EnsembleOptions { Passes = passes, CropScales = [1.0, 0.85, 1.2] });

    private static RecognitionResult Read(decimal value, double confidence) =>
        new(null, value, confidence, "{}", []);

    private sealed class ScriptedRecognizer(IReadOnlyList<RecognitionResult?> script, int failOnPass = -1)
        : IVariantRecognizer
    {
        private int _call;

        public Task<RecognitionResult> RecognizeVariantsAsync(
            MeterSpec meter,
            IReadOnlyList<MeterImage> images,
            CancellationToken ct)
        {
            int index = _call++;

            if (failOnPass >= 0 && (failOnPass == 0 || index == failOnPass - 1))
            {
                throw new RecognitionException($"проход {index + 1} по сценарию теста падает");
            }

            return Task.FromResult(script[index] ?? throw new RecognitionException("сценарий не задал результат"));
        }
    }

    private sealed class RecordingPreprocessor : IImagePreprocessor
    {
        public List<double> Scales { get; } = [];

        public IReadOnlyList<MeterImage> Prepare(ReadOnlyMemory<byte> jpeg, PreprocessOptions options)
        {
            Scales.Add(options.CropScale);
            return [new MeterImage(MeterImageKind.DialCrop, jpeg.ToArray(), 1, 1)];
        }
    }
}
