using System.Globalization;
using System.Text;
using WaterCounters.Core.Metering;
using WaterCounters.Core.Configuration;
using WaterCounters.Recognition.Preprocessing;

namespace WaterCounters.Recognition.Vlm;

/// <summary>Сборка системного и пользовательского сообщений под конкретный счётчик.</summary>
public static class MeterPromptBuilder
{
    public static string System(MeterSpec meter, PromptVariant variant, VlmPass pass = VlmPass.Full)
    {
        ArgumentNullException.ThrowIfNull(meter);

        if (pass == VlmPass.SerialOnly)
        {
            return SerialSystem(meter);
        }

        return variant switch
        {
            PromptVariant.English => EnglishSystem(meter),
            PromptVariant.Terse => TerseSystem(meter),
            _ => RussianSystem(meter),
        };
    }

    public static string User(
        MeterSpec meter,
        PromptVariant variant,
        IReadOnlyList<MeterImage> images,
        VlmPass pass = VlmPass.Full)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(images);

        bool english = variant == PromptVariant.English;
        var text = new StringBuilder();

        text.Append(pass == VlmPass.SerialOnly
            ? "Найди на корпусе счётчика серийный номер и верни его."
            : english
                ? $"Read the {Kind(meter, english: true)} meter."
                : $"Прочитай показание счётчика ({Kind(meter, english: false)}).");

        if (images.Count > 1)
        {
            text.Append(english
                ? " The images show the same meter: "
                : " На снимках один и тот же счётчик: ");

            text.Append(string.Join(
                english ? ", " : ", ",
                images.Select((image, index) => Describe(image, index, english))));

            text.Append(english
                ? ". Use them together; they must agree."
                : ". Пользуйся ими вместе — они обязаны сходиться.");
        }

        return text.ToString();
    }

    private static string Describe(MeterImage image, int index, bool english) =>
        image.Kind switch
        {
            MeterImageKind.DialCrop => english
                ? $"#{index + 1} is a tight crop of the dial"
                : $"№{index + 1} — тесный кроп циферблата",
            _ => english
                ? $"#{index + 1} is the whole meter"
                : $"№{index + 1} — счётчик целиком",
        };

    /// <summary>
    /// Промпт прохода, который читает только серийный номер. Про показание здесь не
    /// говорится ни слова — в этом весь смысл разделения: модель занимается одним
    /// делом. Номер нужен, чтобы понять, какой счётчик на снимке: два холодных и два
    /// горячих в квартире внешне неразличимы.
    /// </summary>
    private static string SerialSystem(MeterSpec meter)
    {
        var text = new StringBuilder();

        text.AppendLine("Ты ищешь на фотографии счётчика его серийный (заводской) номер. Отвечай строго по схеме.");
        text.AppendLine();
        text.AppendLine("Номер напечатан или выгравирован на корпусе, на циферблате или на наклейке. Верни");
        text.AppendLine("только сам номер, без слов «№», «зав. №» и без года выпуска, который печатают рядом.");
        text.AppendLine();
        text.AppendLine("Цифры на барабане счётчика — это показание, а не номер. Не путай их: показание");
        text.AppendLine("меняется от месяца к месяцу, номер напечатан на корпусе навсегда.");
        text.AppendLine();
        text.AppendLine("Если номер не виден или читается неуверенно — верни null и снизь confidence.");
        text.AppendLine("Выдуманный номер хуже отсутствующего: по нему снимок припишут не тому счётчику.");

        return text.ToString();
    }

    private static string RussianSystem(MeterSpec meter)
    {
        var text = new StringBuilder();

        text.AppendLine("Ты читаешь показания квартирного счётчика по фотографии. Отвечай строго по схеме.");
        text.AppendLine();
        text.AppendLine(Format(
            "Разрядность: {0} цифр до запятой, {1} после. Верни их отдельно: integer_part — ровно {0} цифр",
            meter.IntegerDigits,
            meter.FractionDigits));
        text.AppendLine(Format(
            "с ведущими нулями, как на барабане; fractional_part — ровно {0} цифр.",
            meter.FractionDigits));
        text.AppendLine();

        if (meter.Kind is MeterKind.ColdWater or MeterKind.HotWater)
        {
            text.AppendLine("На водосчётчике разряды на красном фоне (или с красными цифрами) — это дробная часть,");
            text.AppendLine("литры. Чёрные разряды — кубометры. Не путай их местами.");
            text.AppendLine();
        }

        text.AppendLine("Если барабан разряда стоит в перекате и видны две цифры сразу — бери меньшую (ту, что");
        text.AppendLine("уходит вверх) и напиши об этом в notes.");
        text.AppendLine();
        text.AppendLine("Серийный номер (serial) верни ровно так, как он напечатан на корпусе, вместе с");
        text.AppendLine("разделителями. Не восстанавливай его по памяти и не дополняй: если номер не виден");
        text.AppendLine("или читается неуверенно — верни null.");
        text.AppendLine();
        text.AppendLine("Ничего не додумывай. Нечитаемую цифру не угадывай: снизь confidence и опиши проблему");
        text.AppendLine("в notes. Неверное показание уходит поставщику необратимо, отсутствующее — просто");
        text.AppendLine("попадает на проверку человеку.");
        text.AppendLine();
        text.AppendLine("digit_confidences — уверенность по каждой цифре слева направо, включая дробные.");

        return text.ToString();
    }

    private static string EnglishSystem(MeterSpec meter)
    {
        var text = new StringBuilder();

        text.AppendLine("You read a household utility meter from a photograph. Answer strictly per the schema.");
        text.AppendLine();
        text.AppendLine(Format(
            "Digits: {0} before the decimal point, {1} after. Return them separately: integer_part must be",
            meter.IntegerDigits,
            meter.FractionDigits));
        text.AppendLine(Format(
            "exactly {0} digits including leading zeros as printed on the drum; fractional_part exactly {1} digits.",
            meter.IntegerDigits,
            meter.FractionDigits));
        text.AppendLine();

        if (meter.Kind is MeterKind.ColdWater or MeterKind.HotWater)
        {
            text.AppendLine("On a water meter the red digits (or digits on a red background) are the fractional");
            text.AppendLine("part — litres. Black digits are cubic metres. Never swap them.");
            text.AppendLine();
        }

        text.AppendLine("If a drum is mid-roll and two digits are visible at once, take the smaller one (the one");
        text.AppendLine("rolling up) and say so in notes.");
        text.AppendLine();
        text.AppendLine("Return serial exactly as printed, separators included. Never reconstruct or complete it:");
        text.AppendLine("if it is not visible or not certain, return null.");
        text.AppendLine();
        text.AppendLine("Never guess. For an unreadable digit lower the confidence and describe the problem in");
        text.AppendLine("notes: a wrong reading is submitted to the utility irreversibly, a missing one is merely");
        text.AppendLine("escalated to a human.");
        text.AppendLine();
        text.AppendLine("digit_confidences — per-digit confidence, left to right, fractional digits included.");

        return text.ToString();
    }

    /// <summary>
    /// Короткий промпт. Выигрывает у подробного заметно — но правило про перекат в нём
    /// оставлено: замер показал, что последний разряд на реальных снимках чаще всего
    /// именно в перекате, и без этой строки модель читает его наугад.
    /// </summary>
    private static string TerseSystem(MeterSpec meter) => Format(
        "Прочитай показание счётчика: {0} цифр до запятой, {1} после. Верни разряды строками с ведущими " +
        "нулями. Если барабан разряда в перекате и видны две цифры сразу — бери нижнюю, ту, что уходит " +
        "вверх. Серийный номер — как напечатан, иначе null. Не угадывай нечитаемое, снижай confidence.",
        meter.IntegerDigits,
        meter.FractionDigits);

    private static string Kind(MeterSpec meter, bool english) => meter.Kind switch
    {
        MeterKind.ColdWater => english ? "cold water" : "холодная вода",
        MeterKind.HotWater => english ? "hot water" : "горячая вода",
        _ => english ? "electricity" : "электричество",
    };

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
