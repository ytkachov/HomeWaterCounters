using System.Text.Json;
using WaterCounters.Core.Configuration;
using WaterCounters.Core.Metering;
using WaterCounters.Core.State;

namespace WaterCounters.Core.Tests;

/// <summary>
/// settings.json правится руками и с телефона, поэтому проверяется не только то, что
/// он разбирается, но и то, что человек в состоянии его прочитать.
/// </summary>
public class AppSettingsTests
{
    /// <summary>Квартира с двумя счётчиками холодной воды, двумя горячей и одним электрическим.</summary>
    private const string FiveMeters = """
        {
          "schemaVersion": 1,
          "revision": 1,
          "meters": [
            {
              "key": "cold-water-kitchen",
              "displayName": "Холодная, кухня",
              "kind": "ColdWater",
              "unit": "м³",
              "integerDigits": 5,
              "fractionDigits": 3,
              "serialNumber": "11-111-111",
              "portalId": "W-1",
              "sortOrder": 0
            },
            {
              "key": "cold-water-bathroom",
              "displayName": "Холодная, ванная",
              "kind": "ColdWater",
              "unit": "м³",
              "integerDigits": 5,
              "fractionDigits": 3,
              "serialNumber": "22-222-222",
              "portalId": "W-2",
              "sortOrder": 1
            },
            {
              "key": "hot-water-kitchen",
              "displayName": "Горячая, кухня",
              "kind": "HotWater",
              "unit": "м³",
              "integerDigits": 5,
              "fractionDigits": 3,
              "serialNumber": "33-333-333",
              "portalId": "W-3",
              "sortOrder": 2
            },
            {
              "key": "hot-water-bathroom",
              "displayName": "Горячая, ванная",
              "kind": "HotWater",
              "unit": "м³",
              "integerDigits": 5,
              "fractionDigits": 3,
              "serialNumber": "44-444-444",
              "portalId": "W-4",
              "sortOrder": 3
            },
            {
              "key": "electricity",
              "displayName": "Электричество",
              "kind": "Electricity",
              "unit": "кВт·ч",
              "integerDigits": 5,
              "fractionDigits": 1,
              "serialNumber": "55-555-555",
              "portalId": "E-1",
              "sortOrder": 4
            }
          ],
          "portal": { "dryRun": true }
        }
        """;

    [Fact]
    public void ReadsAFlatWithTwoMetersOfEachKind()
    {
        AppSettings settings = Parse(FiveMeters);

        Assert.Equal(5, settings.Meters.Count);
        Assert.Equal(2, settings.Meters.Count(m => m.Kind == MeterKind.ColdWater));
        Assert.Equal(2, settings.Meters.Count(m => m.Kind == MeterKind.HotWater));
        Assert.Single(settings.Meters, m => m.Kind == MeterKind.Electricity);
    }

    [Fact]
    public void KeysAndPortalIdsStayDistinct()
    {
        AppSettings settings = Parse(FiveMeters);

        // Совпавший ключ — это перезаписанное показание, совпавший portalId — показание,
        // уехавшее не в то поле кабинета. И то и другое молча портит сразу два счётчика.
        Assert.Equal(5, settings.Meters.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(5, settings.Meters.Select(m => m.PortalId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(5, settings.Meters.Select(m => m.SerialNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void KindIsWrittenAsTextRatherThanANumber()
    {
        string json = JsonSerializer.Serialize(
            Parse(FiveMeters), ConfigurationJsonContext.Default.AppSettings);

        Assert.Contains("\"ColdWater\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\": 0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MeterLookupIsCaseInsensitive()
    {
        AppSettings settings = Parse(FiveMeters);

        Assert.NotNull(settings.MeterByKey("HOT-WATER-KITCHEN"));
        Assert.Null(settings.MeterByKey("hot-water"));
    }

    [Fact]
    public void OrderedMetersFollowTheShootingOrder()
    {
        Assert.Equal(
            new[] { "cold-water-kitchen", "cold-water-bathroom", "hot-water-kitchen", "hot-water-bathroom", "electricity" },
            Parse(FiveMeters).OrderedMeters.Select(m => m.Key));
    }

    [Fact]
    public void MissingSectionsKeepTheirDefaultsInsteadOfBecomingNull()
    {
        // Настройки правятся руками, и секцию легко не дописать. System.Text.Json
        // при десериализации не применяет инициализаторы свойств, поэтому без
        // приведения к норме отсутствующая секция пришла бы как null — и обработчик
        // упал бы на первом же обращении к настройкам.
        // Без WithDefaults: инициализаторы обязаны пережить десериализацию сами.
        AppSettings settings = Parse("""{"meters":[]}""");

        Assert.NotNull(settings.Portal);
        Assert.NotNull(settings.Schedule);
        Assert.NotNull(settings.Recognition);
        Assert.NotNull(settings.Mail);
        Assert.NotNull(settings.Meters);
        Assert.NotNull(settings.UpdatedBy);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void DryRunIsOnUnlessTurnedOffExplicitly()
    {
        // Отправка показаний необратима, поэтому по умолчанию она выключена.
        // Проверяются все три пути: заготовка, файл без секции portal и файл,
        // где секция есть, но флага в ней нет.
        Assert.True(AppSettings.CreateDefault().Portal.DryRun);
        Assert.True(Parse("""{"meters":[]}""").Portal.DryRun);
        Assert.True(Parse("""{"portal":{}}""").Portal.DryRun);
        Assert.False(Parse("""{"portal":{"dryRun":false}}""").Portal.DryRun);
    }

    [Fact]
    public void ExplicitNullSectionStillFallsBackToDefaults()
    {
        // Инициализаторы спасают от отсутствующей секции, но не от написанного
        // руками "portal": null. Для этого и нужно приведение к норме.
        Assert.NotNull(Parse("""{"portal":null,"schedule":null}""").WithDefaults().Portal);
        Assert.True(Parse("""{"portal":null}""").WithDefaults().Portal.DryRun);
    }

    [Fact]
    public void ComputedPropertiesDoNotLeakIntoTheFile()
    {
        // settings.json правится руками и с телефона. Вычисляемые поля в нём — это
        // не только лишний вес: поправив integerDigits, человек увидит рядом старый
        // maxValue и решит, что файл сломан. Считаются они из основных, значит
        // храниться не должны.
        string json = JsonSerializer.Serialize(
            AppSettings.CreateDefault(),
            ConfigurationJsonContext.Default.AppSettings);

        Assert.DoesNotContain("orderedMeters", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maxValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smallestIncrement", json, StringComparison.OrdinalIgnoreCase);

        // А то, что задано человеком, обязано долететь.
        Assert.Contains("integerDigits", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherDefaultsSurviveDeserialisationToo()
    {
        RecognitionSettings recognition = Parse("""{"recognition":{}}""").Recognition;

        Assert.Equal(3, recognition.SettlingMinutes);
        Assert.Equal(0.80, recognition.MinConfidence);
        Assert.True(recognition.Preprocess);

        // Умолчания распознавания выбраны замером по фикстурам, а не на глаз, и
        // менять их без нового замера незачем: снимок не ужимается (барабан занимает
        // малую долю кадра), контекста хватает на полноразмерный кадр, выравнивание
        // яркости выключено, промпт короткий.
        Assert.Equal(4000, recognition.MaxImageDimension);
        Assert.Equal(16384, recognition.ContextTokens);
        Assert.False(recognition.EnhanceDarkFrames);
        Assert.Equal(PromptVariant.Terse, recognition.Prompt);

        Assert.Equal(25, Parse("""{"schedule":{}}""").Schedule.DeadlineDayOfMonth);
        Assert.Equal(587, Parse("""{"mail":{}}""").Mail.SmtpPort);
    }

    [Fact]
    public void NormalisingDoesNotOverwriteWhatTheFileActuallySays()
    {
        AppSettings settings = Parse(FiveMeters).WithDefaults();

        Assert.Equal(5, settings.Meters.Count);
        Assert.Equal(1, settings.Revision);
        Assert.True(settings.Portal.DryRun);
    }

    private static AppSettings Parse(string json) =>
        JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.AppSettings)!;
}

/// <summary>
/// history.json — единственный признак «период уже закрыт». Ошибка при его чтении
/// означает либо повторную отправку показаний, либо пропущенный месяц.
/// </summary>
public class ReadingHistoryTests
{
    [Fact]
    public void EmptyFileMeansOpenPeriodRatherThanACrash()
    {
        ReadingHistory history = Parse("{}");

        Assert.NotNull(history.Readings);
        Assert.NotNull(history.Periods);
        Assert.False(history.IsClosed(new PeriodKey(2026, 7)));
        Assert.Null(history.Latest("cold-water"));
        Assert.Empty(history.For("cold-water"));
    }

    [Fact]
    public void DryRunDoesNotCloseThePeriod()
    {
        // Показания в кабинет не ушли, значит период обязан обработаться заново,
        // когда режим проверки снимут.
        ReadingHistory history = ReadingHistory.Empty.With(
            [],
            new SubmittedPeriod
            {
                Period = new PeriodKey(2026, 7),
                SubmittedUtc = TestData.Epoch,
                WasDryRun = true,
                WasForecast = false,
            },
            TestData.Epoch);

        Assert.False(history.IsClosed(new PeriodKey(2026, 7)));
        Assert.NotNull(history.PeriodRecord(new PeriodKey(2026, 7)));
    }

    [Fact]
    public void ReprocessingAPeriodReplacesRatherThanDuplicates()
    {
        var period = new PeriodKey(2026, 7);

        ReadingHistory history = ReadingHistory.Empty
            .With([Reading(period, 100m)], null, TestData.Epoch)
            .With([Reading(period, 101m)], null, TestData.Epoch);

        // Два значения за один месяц сломали бы расчёт дельт и весь прогноз.
        MeterReading single = Assert.Single(history.For("cold-water"));
        Assert.Equal(101m, single.Value);
    }

    private static MeterReading Reading(PeriodKey period, decimal value) => new()
    {
        MeterKey = "cold-water",
        Period = period,
        Value = value,
        Source = ReadingSource.Recognized,
    };

    private static ReadingHistory Parse(string json) =>
        JsonSerializer.Deserialize(json, StateJsonContext.Default.ReadingHistory)!;
}
