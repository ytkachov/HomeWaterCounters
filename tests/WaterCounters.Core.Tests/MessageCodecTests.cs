using System.Text;
using System.Text.Json;
using WaterCounters.Core.Messaging;
using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Tests;

public class MessageCodecTests
{
    private static readonly PeriodKey Period = new(2026, 7);

    [Fact]
    public void RoundTrip_PreservesEnvelopeAndPayload()
    {
        var payload = new SubmitReadingsPayload
        {
            Photos =
            [
                new PhotoRef
                {
                    MeterKey = "cold-water",
                    PhotoPath = "/photos/2026-07/cold-water.jpg",
                    CapturedUtc = TestData.Epoch,
                },
            ],
        };

        MessageEnvelope original = MessageCodec.Create(
            MessageType.SubmitReadings, Period, "pixel-8", payload, TestData.Epoch);

        MessageEnvelope decoded = MessageCodec.Decode(MessageCodec.Encode(original));

        Assert.Equal(original.MessageId, decoded.MessageId);
        Assert.Equal(MessageType.SubmitReadings, decoded.Type);
        Assert.Equal("2026-07", decoded.Period);
        Assert.Equal("pixel-8", decoded.DeviceId);
        Assert.Equal(Period, decoded.GetPeriod());

        SubmitReadingsPayload roundTripped = decoded.GetPayload<SubmitReadingsPayload>();
        Assert.Single(roundTripped.Photos);
        Assert.Equal("cold-water", roundTripped.Photos[0].MeterKey);
    }

    [Fact]
    public void Encode_WritesEnumsAsStringsAndCamelCase()
    {
        MessageEnvelope envelope = MessageCodec.Create(
            MessageType.SubmitForecast,
            Period,
            "pixel-8",
            new SubmitForecastPayload { Reason = "deadline+5" },
            TestData.Epoch);

        string json = Encoding.UTF8.GetString(MessageCodec.Encode(envelope));

        // Формат — часть контракта между приложениями: он живёт в файлах Dropbox
        // дольше, чем любая из версий приложения.
        Assert.Contains("\"type\": \"SubmitForecast\"", json, StringComparison.Ordinal);
        Assert.Contains("\"messageId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NewMessageId_SortsChronologically()
    {
        string earlier = MessageCodec.NewMessageId(TestData.Epoch);
        string later = MessageCodec.NewMessageId(TestData.Epoch.AddMilliseconds(1));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void DeterministicMessageId_IsStableForSamePeriod()
    {
        string first = MessageCodec.DeterministicMessageId(MessageType.SubmitForecast, Period);
        string second = MessageCodec.DeterministicMessageId(MessageType.SubmitForecast, Period);

        Assert.Equal(first, second);
        Assert.NotEqual(first, MessageCodec.DeterministicMessageId(MessageType.SubmitForecast, Period.Next()));
    }

    [Fact]
    public void Decode_RejectsNewerSchemaVersion()
    {
        // Старое приложение должно внятно сказать «обнови меня», а не молча
        // потерять поля из будущей схемы.
        const string json = """
            {
              "schemaVersion": 99,
              "messageId": "abc",
              "type": "SubmitForecast",
              "period": "2026-07",
              "deviceId": "pixel-8",
              "createdUtc": "2026-01-01T00:00:00+00:00",
              "payload": {}
            }
            """;

        UnsupportedSchemaVersionException ex = Assert.Throws<UnsupportedSchemaVersionException>(
            () => MessageCodec.Decode(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(99, ex.Version);
    }

    [Fact]
    public void Decode_RejectsMalformedPeriod()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "messageId": "abc",
              "type": "SubmitForecast",
              "period": "июль",
              "deviceId": "pixel-8",
              "createdUtc": "2026-01-01T00:00:00+00:00",
              "payload": {}
            }
            """;

        Assert.Throws<MessageFormatException>(() => MessageCodec.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TryDecode_ReportsErrorInsteadOfThrowing()
    {
        bool ok = MessageCodec.TryDecode("не json"u8, out MessageEnvelope? envelope, out string? error);

        Assert.False(ok);
        Assert.Null(envelope);
        Assert.NotNull(error);
    }

    [Fact]
    public void GetPayload_ThrowsWhenPayloadMissing()
    {
        var envelope = new MessageEnvelope
        {
            MessageId = "abc",
            Type = MessageType.SubmitForecast,
            Period = "2026-07",
            DeviceId = "pixel-8",
            CreatedUtc = TestData.Epoch,
            Payload = default,
        };

        Assert.Throws<MessageFormatException>(() => envelope.GetPayload<SubmitForecastPayload>());
    }

    [Fact]
    public void PeriodKey_SerializesAsString()
    {
        var reading = new MeterReading
        {
            MeterKey = "cold-water",
            Period = new PeriodKey(2026, 3),
            Value = 123.456m,
            Source = ReadingSource.Recognized,
        };

        string json = JsonSerializer.Serialize(reading, WaterCountersJsonContext.Default.MeterReading);
        Assert.Contains("\"period\": \"2026-03\"", json, StringComparison.Ordinal);

        MeterReading? back = JsonSerializer.Deserialize(json, WaterCountersJsonContext.Default.MeterReading);
        Assert.Equal(reading, back);
    }
}
