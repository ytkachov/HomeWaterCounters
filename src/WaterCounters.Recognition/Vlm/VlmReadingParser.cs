using System.Globalization;
using System.Text.Json;
using WaterCounters.Core.Metering;

namespace WaterCounters.Recognition.Vlm;

/// <summary>
/// Превращает ответ модели в <see cref="RecognitionResult"/>.
///
/// Значение собирается из строковых разрядов, а не берётся из поля <c>value</c>:
/// разряды модель именно читает, а <c>value</c> — считает, и арифметика у неё
/// получается заметно хуже чтения. Поле <c>value</c> используется как перекрёстная
/// проверка: расхождение попадает в предупреждения.
/// </summary>
internal static class VlmReadingParser
{
    /// <summary>Границы, в которых double безопасно приводится к decimal.</summary>
    private const double SafeMagnitude = 1e18;

    public static RecognitionResult Parse(MeterSpec meter, string rawJson)
    {
        ArgumentNullException.ThrowIfNull(meter);

        string payload = JsonPayload.Unwrap(rawJson);

        VlmReading? reading;

        try
        {
            reading = JsonSerializer.Deserialize(payload, RecognitionJsonContext.Default.VlmReading);
        }
        catch (JsonException ex)
        {
            return RecognitionResult.Failed($"ответ модели не разбирается по схеме: {ex.Message}", rawJson);
        }

        return reading is null
            ? RecognitionResult.Failed("ответ модели пуст", rawJson)
            : Compose(meter, reading, rawJson);
    }

    private static RecognitionResult Compose(MeterSpec meter, VlmReading reading, string rawJson)
    {
        List<string> warnings = [];

        string integerPart = Digits(reading.IntegerPart);

        if (integerPart.Length == 0)
        {
            return RecognitionResult.Failed("модель не вернула целую часть показания", rawJson);
        }

        if (integerPart.Length > meter.IntegerDigits)
        {
            warnings.Add(
                $"прочитано {integerPart.Length} целых разрядов вместо {meter.IntegerDigits} — " +
                "взяты младшие, возможно, в кадр попал серийный номер");
            integerPart = integerPart[^meter.IntegerDigits..];
        }

        string fractionPart = NormalizeFraction(meter, Digits(reading.FractionalPart), warnings);

        decimal value = decimal.Parse(integerPart, CultureInfo.InvariantCulture);

        if (fractionPart.Length > 0)
        {
            value += decimal.Parse(fractionPart, CultureInfo.InvariantCulture) * meter.SmallestIncrement;
        }

        CrossCheckReportedValue(meter, reading, value, warnings);

        if (value > meter.MaxValue)
        {
            warnings.Add($"значение {value} выходит за разрядность счётчика (максимум {meter.MaxValue})");
        }

        double confidence = EffectiveConfidence(reading, warnings);

        // Промпт прямо просит описывать в notes перекат барабана и всё нечитаемое,
        // поэтому заметка модели — не украшение, а то, ради чего поле запрашивалось.
        if (!string.IsNullOrWhiteSpace(reading.Notes))
        {
            warnings.Add($"модель: {reading.Notes.Trim()}");
        }

        return new RecognitionResult(
            Serial: string.IsNullOrWhiteSpace(reading.Serial) ? null : reading.Serial.Trim(),
            Value: value,
            Confidence: confidence,
            RawJson: rawJson,
            Warnings: warnings);
    }

    /// <summary>
    /// Приводит дробную часть ровно к разрядности счётчика. Недобор дополняется нулями
    /// справа, а не слева: красные барабаны читаются слева направо, и "45" на
    /// трёхразрядном счётчике — это 450 литров, а не 45.
    /// </summary>
    private static string NormalizeFraction(MeterSpec meter, string fraction, List<string> warnings)
    {
        if (meter.FractionDigits == 0)
        {
            if (fraction.Length > 0)
            {
                warnings.Add($"у счётчика нет дробных разрядов, прочитанная дробная часть '{fraction}' отброшена");
            }

            return string.Empty;
        }

        if (fraction.Length == meter.FractionDigits)
        {
            return fraction;
        }

        if (fraction.Length > meter.FractionDigits)
        {
            warnings.Add(
                $"прочитано {fraction.Length} дробных разрядов вместо {meter.FractionDigits} — лишние отброшены");
            return fraction[..meter.FractionDigits];
        }

        warnings.Add(fraction.Length == 0
            ? $"дробная часть не прочитана — принята нулевой ({meter.FractionDigits} разряда)"
            : $"прочитано {fraction.Length} дробных разрядов из {meter.FractionDigits} — дополнены нулями справа");

        return fraction.PadRight(meter.FractionDigits, '0');
    }

    private static void CrossCheckReportedValue(
        MeterSpec meter,
        VlmReading reading,
        decimal composed,
        List<string> warnings)
    {
        if (!double.IsFinite(reading.Value) || Math.Abs(reading.Value) >= SafeMagnitude)
        {
            warnings.Add("поле value в ответе модели не является конечным числом");
            return;
        }

        decimal reported = (decimal)reading.Value;

        if (Math.Abs(reported - composed) > meter.SmallestIncrement / 2m)
        {
            warnings.Add(
                $"модель вернула value = {reported}, а из разрядов собирается {composed} — " +
                "взято собранное из разрядов");
        }
    }

    /// <summary>
    /// Показание не надёжнее своей худшей цифры, поэтому за уверенность берётся минимум
    /// из общей оценки и оценок по разрядам. Ниже порога валидатор удержит отправку.
    /// </summary>
    private static double EffectiveConfidence(VlmReading reading, List<string> warnings)
    {
        double confidence = Clamp(reading.Confidence);

        if (reading.DigitConfidences is not { Count: > 0 } digits)
        {
            return confidence;
        }

        double weakest = double.PositiveInfinity;
        int weakestIndex = 0;

        for (int i = 0; i < digits.Count; i++)
        {
            if (digits[i] < weakest)
            {
                weakest = digits[i];
                weakestIndex = i;
            }
        }

        weakest = Clamp(weakest);

        if (weakest >= confidence)
        {
            return confidence;
        }

        warnings.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"разряд №{weakestIndex + 1} прочитан с уверенностью {weakest:P0} — она и взята за общую"));

        return weakest;
    }

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static string Digits(string? source) =>
        string.IsNullOrEmpty(source) ? string.Empty : new string([.. source.Where(char.IsAsciiDigit)]);
}

/// <summary>
/// Достаёт объект JSON из ответа модели. Схема в поле format обязана исключать
/// обрамление, но модели, поднятые за OpenAI-совместимым фасадом, всё равно иногда
/// заворачивают ответ в ```json — а падать из-за трёх обратных кавычек глупо.
/// </summary>
internal static class JsonPayload
{
    public static string Unwrap(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        string trimmed = raw.Trim();

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        int start = trimmed.IndexOf('{', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf('}');

        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }
}
