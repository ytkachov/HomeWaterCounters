using WaterCounters.Core.Metering;

namespace WaterCounters.Core.Tests;

/// <summary>
/// От этого сравнения зависит основной способ работы: снять счётчики в любом порядке
/// и просто положить фотографии в папку. Какой счётчик на снимке, известно только по
/// серийному номеру, и ошибка здесь отправит показания кухни за ванную — молча.
/// </summary>
public sealed class SerialNumberTests
{
    [Fact]
    public void IdenticalNumbersMatch()
    {
        Assert.True(SerialNumber.Matches("150764876", "150764876"));
        Assert.True(SerialNumber.IsExact("150764876", "150764876"));
    }

    [Theory]
    [InlineData("12-345-678", "12345678")]
    [InlineData("№ 12345678", "12345678")]
    [InlineData("12 345 678", "12-345-678")]
    public void PunctuationAndSpacingAreIgnored(string expected, string actual)
    {
        // На корпусе номер печатают с дефисами, модель возвращает как придётся.
        Assert.True(SerialNumber.Matches(expected, actual));
    }

    [Fact]
    public void NumberPrintedNextToTheYearIsStillRecognised()
    {
        // Реальный ответ модели по электросчётчику: на наклейке рядом с номером
        // напечатан год выпуска, и модель честно возвращает всё, что видит.
        Assert.True(SerialNumber.Matches("58016833", "2018г. №58016833"));

        // Но это уже не точное совпадение, и при выборе счётчика уступает точному.
        Assert.False(SerialNumber.IsExact("58016833", "2018г. №58016833"));
    }

    [Fact]
    public void ShortNumbersAreComparedWholeOnly()
    {
        // Четыре цифры найдутся в чём угодно — в годе выпуска, в номере ГОСТа,
        // в штрихкоде. Совпадение по вхождению здесь означало бы взять чужой счётчик.
        Assert.False(SerialNumber.Matches("2018", "2018г. №58016833"));
        Assert.True(SerialNumber.Matches("2018", "2018"));
    }

    [Fact]
    public void MissingNumberNeverMatches()
    {
        // Не прочитанный номер — это «неизвестно», а не «подходит любой».
        Assert.False(SerialNumber.Matches("150764876", null));
        Assert.False(SerialNumber.Matches(null, "150764876"));
        Assert.False(SerialNumber.Matches(null, null));
        Assert.False(SerialNumber.Matches("150764876", "   "));
    }

    [Fact]
    public void DifferentMetersDoNotMatch()
    {
        // Два холодных счётчика одной квартиры: перепутать их — отправить показания
        // кухни за ванную.
        Assert.False(SerialNumber.Matches("150764876", "150790241"));
    }
}
