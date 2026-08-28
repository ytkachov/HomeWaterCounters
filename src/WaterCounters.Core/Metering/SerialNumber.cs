namespace WaterCounters.Core.Metering;

/// <summary>
/// Сопоставление серийного номера, прочитанного на фотографии, с номером из настроек.
///
/// От этого сравнения зависит основной способ работы: человек снимает счётчики в
/// любом порядке, кладёт фотографии в общую папку и больше ничего не делает. Какой
/// счётчик на снимке, известно только по серийному номеру — два холодных и два
/// горячих в одной квартире внешне неразличимы, и ошибка здесь тихо отправит
/// показания кухни за ванную.
/// </summary>
public static class SerialNumber
{
    /// <summary>
    /// Короткие номера сравниваются только целиком. Вхождение из трёх-четырёх цифр
    /// найдётся в чём угодно — в годе выпуска, в номере ГОСТа, в штрихкоде.
    /// </summary>
    private const int MinLengthForPartialMatch = 5;

    /// <summary>Только буквы и цифры: пробелы, дефисы и «№» на корпусе и в ответе модели ставятся по-разному.</summary>
    public static string Normalize(string? serial) =>
        string.IsNullOrEmpty(serial) ? string.Empty : new string([.. serial.Where(char.IsLetterOrDigit)]);

    /// <summary>Номера совпали посимвольно — самый надёжный случай.</summary>
    public static bool IsExact(string? expected, string? actual)
    {
        string want = Normalize(expected);
        string got = Normalize(actual);

        return want.Length > 0 && want.Equals(got, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Номера совпали — целиком или так, что напечатанный на корпусе содержится в
    /// прочитанном.
    ///
    /// Вхождение допускается потому, что на наклейке рядом с номером печатают год
    /// выпуска и серию, и модель честно возвращает всё, что видит: «2018г. №58016833»
    /// при номере «58016833». Требовать точного совпадения строки целиком означало бы
    /// отвергать верно прочитанный номер и терять снимок.
    /// </summary>
    public static bool Matches(string? expected, string? actual)
    {
        if (IsExact(expected, actual))
        {
            return true;
        }

        string want = Normalize(expected);
        string got = Normalize(actual);

        return want.Length >= MinLengthForPartialMatch
            && got.Contains(want, StringComparison.OrdinalIgnoreCase);
    }
}
