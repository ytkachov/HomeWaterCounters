using WaterCounters.Core.Metering;
using WaterCounters.Core.Validation;

namespace WaterCounters.Core.Tests;

public class ReadingValidatorTests
{
    private readonly ReadingValidator _validator = new();

    private static List<MeterReading> SteadyHistory() =>
        TestData.HistoryFor(TestData.ColdWater, new PeriodKey(2026, 1), 100m, 105m, 110m, 115m);

    private static void AssertHas(IReadOnlyList<ValidationIssue> issues, string code, ValidationSeverity severity)
    {
        ValidationIssue issue = Assert.Single(issues, i => i.Code == code);
        Assert.Equal(severity, issue.Severity);
    }

    [Fact]
    public void CleanReading_ProducesNoIssues()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 120m, SteadyHistory(), recognizedSerial: "12-345-678", confidence: 0.97);

        Assert.Empty(issues);
    }

    [Fact]
    public void BelowPrevious_IsCritical()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(TestData.ColdWater, 90m, SteadyHistory());

        AssertHas(issues, ReadingValidator.CodeBelowPrevious, ValidationSeverity.Critical);
    }

    [Fact]
    public void ZeroConsumption_IsWarning()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(TestData.ColdWater, 115m, SteadyHistory());

        AssertHas(issues, ReadingValidator.CodeZeroConsumption, ValidationSeverity.Warning);
    }

    [Fact]
    public void DeltaFarAboveMedian_IsWarning()
    {
        // Обычно 5 м³, здесь 50 — самый частый вид ошибки распознавания
        // (лишний разряд или спутанная цифра).
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(TestData.ColdWater, 165m, SteadyHistory());

        AssertHas(issues, ReadingValidator.CodeDeltaTooLarge, ValidationSeverity.Warning);
    }

    [Fact]
    public void DeltaWithinTolerance_IsAccepted()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 130m, SteadyHistory(), recognizedSerial: "12-345-678");

        Assert.Empty(issues);
    }

    [Fact]
    public void SerialMismatch_IsCritical()
    {
        // Несовпадение серийника почти всегда значит, что счётчики сняты не в том
        // порядке — это опаснее неверной цифры, потому что оба показания уйдут не туда.
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 120m, SteadyHistory(), recognizedSerial: "99-999-999");

        AssertHas(issues, ReadingValidator.CodeSerialMismatch, ValidationSeverity.Critical);
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("12 345 678")]
    [InlineData("12-345-678")]
    [InlineData("12/345/678")]
    public void SerialComparison_IgnoresSeparators(string recognized)
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 120m, SteadyHistory(), recognizedSerial: recognized);

        Assert.Empty(issues);
    }

    [Fact]
    public void SerialMissing_IsInfoOnly()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 120m, SteadyHistory(), recognizedSerial: null);

        AssertHas(issues, ReadingValidator.CodeSerialMissing, ValidationSeverity.Info);
    }

    [Fact]
    public void SerialNotConfigured_ProducesNoIssue()
    {
        List<MeterReading> history = TestData.HistoryFor(
            TestData.Electricity, new PeriodKey(2026, 1), 5000m, 5300m, 5600m);

        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.Electricity, 5900m, history, recognizedSerial: null);

        Assert.Empty(issues);
    }

    [Fact]
    public void LowConfidence_IsWarning()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 120m, SteadyHistory(), recognizedSerial: "12-345-678", confidence: 0.42);

        AssertHas(issues, ReadingValidator.CodeLowConfidence, ValidationSeverity.Warning);
    }

    [Fact]
    public void ValueBeyondMeterRange_IsCritical()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 1_000_000m, SteadyHistory());

        AssertHas(issues, ReadingValidator.CodeOutOfRange, ValidationSeverity.Critical);
    }

    [Fact]
    public void ValueNotMultipleOfIncrement_IsWarning()
    {
        // У счётчика три знака после запятой — 120.4567 прочитано неверно.
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(
            TestData.ColdWater, 120.4567m, SteadyHistory());

        AssertHas(issues, ReadingValidator.CodeDigitCountMismatch, ValidationSeverity.Warning);
    }

    [Fact]
    public void FirstReadingEver_IsInfoNotWarning()
    {
        IReadOnlyList<ValidationIssue> issues = _validator.Validate(TestData.ColdWater, 120m, []);

        AssertHas(issues, ReadingValidator.CodeNoHistory, ValidationSeverity.Info);
    }
}
