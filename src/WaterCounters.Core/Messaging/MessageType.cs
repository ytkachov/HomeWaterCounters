using System.Text.Json.Serialization;

namespace WaterCounters.Core.Messaging;

/// <summary>
/// Тип сообщения в очереди Dropbox. Значения сериализуются строками — менять
/// написание нельзя, иначе сломается совместимость с уже лежащими файлами.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageType>))]
public enum MessageType
{
    // Мобильное → десктоп
    SubmitReadings,
    SubmitForecast,
    ReadingsConfirmed,
    ReadingsRejected,

    // Десктоп → мобильное
    ReadingsProposed,
    ForecastUnavailable,
    SubmissionSucceeded,
    SubmissionFailed,
    DesktopStatus,
}

public static class MessageTypeExtensions
{
    /// <summary>Направление сообщения определяет, в какую папку очереди оно кладётся.</summary>
    public static bool IsToDesktop(this MessageType type) => type is
        MessageType.SubmitReadings or
        MessageType.SubmitForecast or
        MessageType.ReadingsConfirmed or
        MessageType.ReadingsRejected;

    public static bool IsToMobile(this MessageType type) => !type.IsToDesktop();
}
