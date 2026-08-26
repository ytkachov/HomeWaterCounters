using System.Globalization;
using System.IO;
using System.Text;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Storage;
using WaterCounters.Core.Validation;
using WaterCounters.Desktop.Mail;
using WaterCounters.Desktop.Photos;
using WaterCounters.Desktop.State;

namespace WaterCounters.Desktop.Processing;

/// <summary>
/// Письмо о том, что произошло с периодом.
///
/// Уходит и при успехе, и при провале: пока телефона нет, это единственный канал,
/// по которому человек узнаёт, что счётчики сданы, или что модель прочитала не то.
/// При провале прикладываются скриншот и путь к trace — разбирать сбой автоматики
/// на чужом сайте без них нечем.
/// </summary>
public static class ReportComposer
{
    public static MailContent Compose(
        PeriodKey period,
        PipelineResult result,
        PhotoBatchDecision? batch,
        PortalOutcome? portal,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);

        var body = new StringBuilder();

        body.AppendLine(Headline(period, result));
        body.AppendLine();
        body.AppendLine(result.Summary);
        body.AppendLine();

        AppendReadings(body, result, settings);
        AppendBatch(body, batch);
        AppendPortal(body, portal, settings, result);

        return new MailContent
        {
            Subject = Subject(period, result),
            BodyText = body.ToString(),
            Attachments = Attachments(portal, result),
        };
    }

    private static string Subject(PeriodKey period, PipelineResult result) => result.Outcome switch
    {
        SubmissionOutcome.Submitted => $"Показания за {period} переданы",
        SubmissionOutcome.DryRun => $"Показания за {period}: проверка, отправки не было",
        SubmissionOutcome.AlreadySubmitted => $"Показания за {period} уже были сданы",
        SubmissionOutcome.AwaitingConfirmation => $"Показания за {period} ждут подтверждения",
        SubmissionOutcome.HeldForReview => $"Показания за {period} требуют внимания",
        _ => $"Показания за {period}: сбой",
    };

    private static string Headline(PeriodKey period, PipelineResult result) => result.Outcome switch
    {
        SubmissionOutcome.Submitted =>
            $"Показания за {period} приняты личным кабинетом.",
        SubmissionOutcome.DryRun =>
            $"Режим проверки: форма за {period} заполнена, кнопка отправки не нажималась. " +
            "Сверьте цифры и скриншот; чтобы включить настоящую отправку, снимите portal.dryRun.",
        SubmissionOutcome.AlreadySubmitted =>
            $"Период {period} в кабинете уже закрыт — повторная отправка не выполнялась.",
        SubmissionOutcome.AwaitingConfirmation =>
            $"Распознанные показания за {period} отправлены на телефон и ждут подтверждения.",
        SubmissionOutcome.HeldForReview =>
            $"Отправка за {period} удержана. Проверьте отмеченные ниже показания.",
        _ =>
            $"Передать показания за {period} не удалось.",
    };

    private static void AppendReadings(StringBuilder body, PipelineResult result, AppSettings settings)
    {
        if (result.Readings.Count == 0)
        {
            return;
        }

        body.AppendLine("Показания");
        body.AppendLine(new string('-', 60));

        foreach (ReadingCandidate reading in result.Readings.OrderBy(r => r.Meter.SortOrder))
        {
            body.Append("  ").Append(reading.Meter.DisplayName).Append(": ");

            if (reading.Value is { } value)
            {
                body.Append(Number(value)).Append(' ').Append(reading.Meter.Unit);

                if (reading.Delta is { } delta)
                {
                    body.Append(CultureInfo.InvariantCulture, $" (за период {Number(delta)}");

                    if (reading.PreviousValue is { } previous)
                    {
                        body.Append(CultureInfo.InvariantCulture, $", было {Number(previous)}");
                    }

                    body.Append(')');
                }

                if (reading.Confidence is { } confidence)
                {
                    body.Append(CultureInfo.InvariantCulture, $", уверенность {confidence:P0}");
                }
            }
            else
            {
                body.Append("не получено");
            }

            body.AppendLine();

            if (reading.Failure is { } failure)
            {
                body.Append("      ! ").AppendLine(failure);
            }

            foreach (ValidationIssue issue in reading.Issues.OrderByDescending(static i => i.Severity))
            {
                body.Append("      ").Append(Marker(issue.Severity)).Append(' ').AppendLine(issue.Message);
            }

            foreach (string warning in reading.Warnings)
            {
                body.Append("      · ").AppendLine(warning);
            }
        }

        body.AppendLine();
        body.AppendLine($"Модель: {settings.Recognition.Model} ({settings.Recognition.Provider}).");
        body.AppendLine();
    }

    private static void AppendBatch(StringBuilder body, PhotoBatchDecision? batch)
    {
        if (batch is null)
        {
            return;
        }

        body.AppendLine($"Пачка фотографий: {batch.Reason}.");

        if (batch.MissingMeters.Count > 0)
        {
            body.AppendLine(
                "  Без фотографии: " +
                string.Join(", ", batch.MissingMeters.Select(static m => m.DisplayName)));
        }

        if (batch.Unassigned.Count > 0)
        {
            body.AppendLine(
                "  Файлы, не привязанные к счётчику: " +
                string.Join(", ", batch.Unassigned.Select(static e => RemotePath.GetFileName(e.Path))));
        }

        body.AppendLine();
    }

    private static void AppendPortal(
        StringBuilder body,
        PortalOutcome? portal,
        AppSettings settings,
        PipelineResult result)
    {
        body.AppendLine("Кабинет");
        body.AppendLine(new string('-', 60));
        body.AppendLine($"  Режим проверки (dryRun): {(settings.Portal.DryRun ? "включён" : "выключен")}");

        if (portal is null)
        {
            body.AppendLine("  Обращения к кабинету не было.");
            body.AppendLine();
            return;
        }

        body.AppendLine($"  Исход: {result.Outcome}");

        if (portal.Message is { } message)
        {
            body.AppendLine($"  Сообщение кабинета: {message}");
        }

        if (portal.Error is { } error)
        {
            body.AppendLine($"  Ошибка: {error}");
        }

        if (portal.TracePath is { } trace)
        {
            body.AppendLine($"  Trace Playwright: {trace}");
        }

        body.AppendLine();
    }

    private static IReadOnlyList<MailAttachment> Attachments(PortalOutcome? portal, PipelineResult result)
    {
        List<MailAttachment> attachments = [];

        if (portal?.Screenshot is { Length: > 0 } screenshot)
        {
            attachments.Add(new MailAttachment("portal.png", "image/png", screenshot));
        }

        foreach (ReadingCandidate reading in result.Readings.Where(static r => r.Crop is { Length: > 0 }))
        {
            attachments.Add(new MailAttachment($"{reading.Meter.Key}.jpg", "image/jpeg", reading.Crop!));
        }

        return attachments;
    }

    private static string Marker(ValidationSeverity severity) => severity switch
    {
        ValidationSeverity.Critical => "!!",
        ValidationSeverity.Warning => "!",
        _ => "·",
    };

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
