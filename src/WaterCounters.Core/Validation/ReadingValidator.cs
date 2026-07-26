using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Validation;

public enum ValidationSeverity
{
    /// <summary>Стоит показать, но само по себе не повод для правки.</summary>
    Info = 0,

    /// <summary>Требует внимания на экране подтверждения.</summary>
    Warning = 1,

    /// <summary>Значение почти наверняка неверно. Подсвечивается красным.</summary>
    Critical = 2,
}

public sealed record ValidationIssue
{
    public required string Code { get; init; }

    public required ValidationSeverity Severity { get; init; }

    public required string Message { get; init; }
}

public sealed record ValidationOptions
{
    /// <summary>Во сколько раз дельта может превысить медиану, прежде чем это станет подозрительным.</summary>
    public decimal MaxDeltaMultiplier { get; init; } = 3m;

    /// <summary>Ниже этого порога уверенность модели считается недостаточной.</summary>
    public double MinConfidence { get; init; } = 0.80;

    /// <summary>Сколько последних дельт берём как базу для сравнения.</summary>
    public int RecentWindow { get; init; } = 6;
}

/// <summary>
/// Проверки показания перед отправкой на подтверждение. Ничего не блокирует —
/// решение всегда за человеком на экране подтверждения; задача валидатора в том,
/// чтобы человек смотрел именно туда, куда надо.
/// </summary>
public sealed class ReadingValidator(ValidationOptions? options = null)
{
    public const string CodeBelowPrevious = "below-previous";
    public const string CodeDeltaTooLarge = "delta-too-large";
    public const string CodeZeroConsumption = "zero-consumption";
    public const string CodeDigitCountMismatch = "digit-count-mismatch";
    public const string CodeSerialMismatch = "serial-mismatch";
    public const string CodeSerialMissing = "serial-missing";
    public const string CodeLowConfidence = "low-confidence";
    public const string CodeOutOfRange = "out-of-range";
    public const string CodeNoHistory = "no-history";

    private readonly ValidationOptions _options = options ?? new ValidationOptions();

    public IReadOnlyList<ValidationIssue> Validate(
        MeterSpec meter,
        decimal value,
        IReadOnlyCollection<MeterReading> history,
        string? recognizedSerial = null,
        double? confidence = null)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(history);

        List<ValidationIssue> issues = [];

        ValidateRange(meter, value, issues);
        ValidateAgainstHistory(meter, value, history, issues);
        ValidateSerial(meter, recognizedSerial, issues);
        ValidateConfidence(confidence, issues);

        return issues;
    }

    private void ValidateRange(MeterSpec meter, decimal value, List<ValidationIssue> issues)
    {
        if (value < 0 || value > meter.MaxValue)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeOutOfRange,
                Severity = ValidationSeverity.Critical,
                Message = $"Значение {value} вне диапазона счётчика (0…{meter.MaxValue}).",
            });
            return;
        }

        // Число знаков после запятой должно совпадать с ценой деления: у счётчика с
        // тремя дробными разрядами не может быть значения 123.4567.
        decimal remainder = value % meter.SmallestIncrement;

        if (remainder != 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeDigitCountMismatch,
                Severity = ValidationSeverity.Warning,
                Message =
                    $"Значение {value} не кратно цене деления {meter.SmallestIncrement} " +
                    $"({meter.FractionDigits} знака после запятой).",
            });
        }
    }

    private void ValidateAgainstHistory(
        MeterSpec meter,
        decimal value,
        IReadOnlyCollection<MeterReading> history,
        List<ValidationIssue> issues)
    {
        List<MeterReading> ordered =
        [
            .. history.Where(r => r.MeterKey == meter.Key).OrderBy(r => r.Period)
        ];

        if (ordered.Count == 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeNoHistory,
                Severity = ValidationSeverity.Info,
                Message = "Это первое показание — сравнивать не с чем.",
            });
            return;
        }

        MeterReading previous = ordered[^1];
        decimal delta = value - previous.Value;

        if (delta < 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeBelowPrevious,
                Severity = ValidationSeverity.Critical,
                Message =
                    $"Показание {value} меньше предыдущего {previous.Value}. " +
                    "Возможна замена счётчика, переполнение барабана или ошибка распознавания.",
            });
            return;
        }

        if (delta == 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeZeroConsumption,
                Severity = ValidationSeverity.Warning,
                Message = "Нулевое потребление за период — проверьте, что цифры прочитаны верно.",
            });
            return;
        }

        List<decimal> deltas = [];

        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Period.MonthsSince(ordered[i - 1].Period) != 1)
            {
                continue;
            }

            decimal step = ordered[i].Value - ordered[i - 1].Value;

            if (step >= 0)
            {
                deltas.Add(step);
            }
        }

        if (deltas.Count == 0)
        {
            return;
        }

        decimal median = Median([.. deltas.TakeLast(_options.RecentWindow)]);

        if (median > 0 && delta > median * _options.MaxDeltaMultiplier)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeDeltaTooLarge,
                Severity = ValidationSeverity.Warning,
                Message =
                    $"Потребление {delta} превышает обычное ({median}) более чем " +
                    $"в {_options.MaxDeltaMultiplier} раза.",
            });
        }
    }

    private static void ValidateSerial(MeterSpec meter, string? recognizedSerial, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(meter.SerialNumber))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(recognizedSerial))
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeSerialMissing,
                Severity = ValidationSeverity.Info,
                Message = "Серийный номер на фото не прочитан — сверить не с чем.",
            });
            return;
        }

        if (!NormalizeSerial(recognizedSerial).Equals(NormalizeSerial(meter.SerialNumber), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeSerialMismatch,
                Severity = ValidationSeverity.Critical,
                Message =
                    $"Серийный номер на фото ({recognizedSerial}) не совпадает с настроенным " +
                    $"({meter.SerialNumber}). Скорее всего, счётчики перепутаны местами.",
            });
        }
    }

    private void ValidateConfidence(double? confidence, List<ValidationIssue> issues)
    {
        if (confidence is { } value && value < _options.MinConfidence)
        {
            issues.Add(new ValidationIssue
            {
                Code = CodeLowConfidence,
                Severity = ValidationSeverity.Warning,
                Message = $"Модель не уверена в распознавании ({value:P0}). Проверьте цифры внимательно.",
            });
        }
    }

    /// <summary>Серийники печатают с пробелами и дефисами по-разному — сравниваем только буквы и цифры.</summary>
    private static string NormalizeSerial(string serial) =>
        new([.. serial.Where(char.IsLetterOrDigit)]);

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        int middle = values.Count / 2;

        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2m;
    }
}
