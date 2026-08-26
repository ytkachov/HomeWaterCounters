using System.Net.Http;
using Microsoft.Extensions.Logging;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;
using WaterCounters.Desktop.Configuration;
using WaterCounters.Recognition;

namespace WaterCounters.Desktop.Processing;

/// <summary>
/// Распознаватель, пересобираемый при смене настроек.
///
/// Модель, адрес VLM-хоста и число проходов ансамбля правятся с телефона, и правка
/// обязана вступать в силу без перезапуска обработчика. <see cref="RecognitionSettings"/>
/// — record, поэтому «настройки те же» проверяется сравнением значений, а не флагом,
/// который однажды забудут выставить.
/// </summary>
public sealed class RecognizerProvider(
    ISettingsProvider settings,
    IHttpClientFactory clients,
    DesktopOptions options,
    ILogger<RecognizerProvider> logger) : IMeterRecognizer
{
    private readonly ISettingsProvider _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IHttpClientFactory _clients = clients ?? throw new ArgumentNullException(nameof(clients));
    private readonly DesktopOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<RecognizerProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly object _gate = new();

    private RecognitionSettings? _built;
    private IMeterRecognizer? _recognizer;

    public Task<RecognitionResult> RecognizeAsync(MeterSpec meter, ReadOnlyMemory<byte> jpeg, CancellationToken ct) =>
        Current().RecognizeAsync(meter, jpeg, ct);

    private IMeterRecognizer Current()
    {
        RecognitionSettings current = _settings.Current.Recognition;

        lock (_gate)
        {
            if (_recognizer is not null && _built == current)
            {
                return _recognizer;
            }

            HttpClient http = _clients.CreateClient(nameof(IMeterRecognizer));

            // Таймаут держит сам распознаватель на своём токене: клиенту обрывать
            // раньше нечего — крупная модель на слабой карте честно думает минутами.
            http.Timeout = Timeout.InfiniteTimeSpan;

            _recognizer = RecognizerFactory.Create(
                current,
                http,
                _options.FixturesDirectory,
                message => _logger.LogInformation("{Message}", message));

            _built = current;
            return _recognizer;
        }
    }
}
