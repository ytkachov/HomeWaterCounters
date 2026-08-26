namespace WaterCounters.Recognition.Tests;

public class StubRecognizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "wc-fixtures-" + Guid.NewGuid().ToString("N")[..8]);

    public StubRecognizerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("cold-water_01234.567_12-345-678.jpg", "cold-water", 1234.567, "12-345-678")]
    [InlineData("electricity_004521.3_ABC123.jpg", "electricity", 4521.3, "ABC123")]
    [InlineData("hot-water_99.001.jpg", "hot-water", 99.001, null)]
    public void ParsesTheFixtureNaming(string fileName, string meterKey, double value, string? serial)
    {
        Assert.True(StubRecognizer.TryParseFixtureName(fileName, out FixtureExpectation? expectation));
        Assert.Equal(meterKey, expectation.MeterKey);
        Assert.Equal((decimal)value, expectation.Value);
        Assert.Equal(serial, expectation.Serial);
    }

    [Theory]
    [InlineData("IMG_1234.jpg")]
    [InlineData("readme.txt")]
    [InlineData("cold-water.jpg")]
    public void IgnoresFilesThatAreNotMarkedUp(string fileName) =>
        Assert.False(StubRecognizer.TryParseFixtureName(fileName, out _));

    [Fact]
    public async Task ReturnsTheValueMarkedUpInTheFixtureName()
    {
        byte[] content = RecognitionTestData.SyntheticMeterJpeg(120, 90);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "cold-water_01234.567_12-345-678.jpg"), content);

        StubRecognizer recognizer = StubRecognizer.FromFixtures(_directory);
        RecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionTestData.ColdWater, content, CancellationToken.None);

        Assert.Equal(1234.567m, result.Value);
        Assert.Equal("12-345-678", result.Serial);
        Assert.True(result.Confidence > 0.9);
    }

    [Fact]
    public async Task UnknownPhotoYieldsNoValueRatherThanAGuess()
    {
        StubRecognizer recognizer = StubRecognizer.FromFixtures(_directory);

        RecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionTestData.ColdWater, RecognitionTestData.OpaqueJpeg, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("не найдена", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FixedStubAnswersForAnyPhoto()
    {
        StubRecognizer recognizer = StubRecognizer.Fixed(77.007m, "SER-1");

        RecognitionResult result = await recognizer.RecognizeAsync(
            RecognitionTestData.ColdWater, RecognitionTestData.OpaqueJpeg, CancellationToken.None);

        Assert.Equal(77.007m, result.Value);
        Assert.Equal("SER-1", result.Serial);
    }

    [Fact]
    public void MissingFixtureDirectoryIsNotAnError() =>
        Assert.Empty(StubRecognizer.FromFixtures(Path.Combine(_directory, "нет-такой-папки")).Fixtures);
}
