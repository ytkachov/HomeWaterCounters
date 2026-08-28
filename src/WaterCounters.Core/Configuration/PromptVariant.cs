using System.Text.Json.Serialization;

namespace WaterCounters.Core.Configuration;

/// <summary>
/// Вариант промпта. Их несколько не для гибкости, а потому что выбор промпта —
/// такой же замеряемый параметр, как выбор модели: bench-харнесс гоняет комбинации
/// модель × промпт × препроцессинг по фикстурам и сравнивает долю точных совпадений.
///
/// Живёт в настройках, а не в коде распознавания: замер показал, что выбор промпта
/// решает не меньше выбора модели, а значит его должно быть видно и с телефона.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptVariant>))]
public enum PromptVariant
{
    Russian = 0,
    English = 1,

    /// <summary>Только разрядность и запрет домысливать. Проверяет, не мешает ли модели длинный текст.</summary>
    Terse = 2,
}
