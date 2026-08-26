using System.IO;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using WaterCounters.Core.Configuration;
using WaterCounters.Desktop.Configuration;

namespace WaterCounters.Desktop.Mail;

public sealed record MailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record MailContent
{
    public required string Subject { get; init; }

    public required string BodyText { get; init; }

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
}

public interface IMailer
{
    Task SendAsync(MailContent content, CancellationToken ct = default);
}

/// <summary>
/// Отправка отчёта по SMTP.
///
/// Письмо уходит и при успехе, и при провале — это единственный канал, по которому
/// человек узнаёт о происходящем, пока телефона нет. Поэтому сбой отправки письма
/// не должен ронять обработку: он логируется и на этом всё.
/// </summary>
public sealed class SmtpMailer(
    ISettingsProvider settings,
    ILogger<SmtpMailer> logger) : IMailer
{
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ILogger<SmtpMailer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task SendAsync(MailContent content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        MailSettings mail = _settings.Current.Mail;

        if (!mail.Enabled || string.IsNullOrWhiteSpace(mail.To) || string.IsNullOrWhiteSpace(mail.SmtpHost))
        {
            _logger.LogInformation("Почта не настроена — отчёт «{Subject}» не отправлен.", content.Subject);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(mail.From ?? mail.To));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = content.Subject;

        var body = new BodyBuilder { TextBody = content.BodyText };

        foreach (MailAttachment attachment in content.Attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        message.Body = body.ToMessageBody();

        try
        {
            using var client = new SmtpClient();

            await client.ConnectAsync(
                mail.SmtpHost,
                mail.SmtpPort,
                mail.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,
                ct).ConfigureAwait(false);

            string? password = _settings.Secrets?.SmtpPassword;

            if (!string.IsNullOrEmpty(password))
            {
                await client.AuthenticateAsync(mail.UserName ?? mail.From ?? mail.To, password, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            _logger.LogInformation("Отчёт «{Subject}» отправлен на {To}.", content.Subject, mail.To);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ловится всё подряд намеренно. Отправка письма — уведомление о работе,
            // а не сама работа: упавший SMTP не должен уронить уже распознанные
            // показания. Список конкретных типов у MailKit длинный и меняется от
            // версии к версии, а поведение здесь во всех случаях одно.
            _logger.LogError(ex, "Не удалось отправить отчёт «{Subject}».", content.Subject);
        }
    }
}

/// <summary>Заглушка для прогонов без почтового сервера: письма складываются в память.</summary>
public sealed class NullMailer : IMailer
{
    public List<MailContent> Sent { get; } = [];

    public Task SendAsync(MailContent content, CancellationToken ct = default)
    {
        Sent.Add(content);
        return Task.CompletedTask;
    }
}
