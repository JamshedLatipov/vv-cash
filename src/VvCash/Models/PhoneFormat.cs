using System;
using System.Collections.Generic;
using System.Text;

namespace VvCash.Models;

/// <summary>Как на этой кассе выглядит телефон клиента: сколько в нём цифр, как
/// они группируются на экране и какой код страны приклеивается перед отправкой.
///
/// Конструктор, а не init-свойства как у CustomerPrefill: DigitCount считается
/// из Mask один раз, а объект с init-свойствами вычислить его при создании не
/// может. Записи каталога строятся только в коде и никогда не десериализуются,
/// так что терять поддержку object initializer здесь нечего.</summary>
public sealed class PhoneFormat
{
    /// <summary>В Mask символ '#' — место под цифру, всё остальное литерал.</summary>
    private const char DigitSlot = '#';

    public PhoneFormat(string id, string displayName, string countryCode, string mask)
    {
        Id = id;
        DisplayName = displayName;
        CountryCode = countryCode;
        Mask = mask;

        var count = 0;
        foreach (var c in mask)
        {
            if (c == DigitSlot) count++;
        }
        DigitCount = count;

        Placeholder = Format(string.Empty);
    }

    /// <summary>То, что ложится в настройки. Хранится он, а не DisplayName:
    /// переименование страны в интерфейсе не должно ломать настроенную кассу.</summary>
    public string Id { get; }

    /// <summary>Не переводится и лежит в коде. Код страны идёт первым, потому
    /// что это единственная часть строки, которая опознаётся независимо от
    /// письменности; латинские ISO-коды в скобках делают запись читаемой в
    /// локалях en и uz (последняя — латиница) без отдельного ключа i18n на
    /// каждую страну.</summary>
    public string DisplayName { get; }

    /// <summary>Без плюса: приклеивается к цифрам перед отправкой на сервер.</summary>
    public string CountryCode { get; }

    /// <summary>Только национальная часть, без кода страны.</summary>
    public string Mask { get; }

    public int DigitCount { get; }

    /// <summary>Как выглядит пустое поле. Оно же — то, что видит кассир до
    /// первого нажатия на нумпад.</summary>
    public string Placeholder { get; }

    /// <summary>Раскладывает набранные цифры по маске слева направо; на
    /// незанятые места ставит подчёркивания, чтобы было видно, сколько ещё
    /// набирать. Лишние цифры сверх DigitCount отбрасываются — строка приходит
    /// не только с нумпада, но и из префилла строки поиска.</summary>
    public string Format(string? digits)
    {
        var entered = digits ?? string.Empty;
        var result = new StringBuilder("+").Append(CountryCode).Append(' ');

        var next = 0;
        foreach (var c in Mask)
        {
            if (c == DigitSlot)
            {
                result.Append(next < entered.Length ? entered[next] : '_');
                next++;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}

/// <summary>Каталог форматов. Не редактируется из интерфейса сознательно:
/// кассир не должен иметь возможности задать маску, которой не бывает. Новая
/// страна — правка этого файла и релиз.</summary>
public static class PhoneFormats
{
    /// <summary>Казахстан не отдельной записью: там тот же +7 и те же десять
    /// цифр, отдельный пункт делал бы вид, что выбор на что-то влияет.</summary>
    public static readonly PhoneFormat RussiaKazakhstan =
        new("RU", "+7 — Россия / Казахстан (RU / KZ)", "7", "(###) ###-##-##");

    public static readonly PhoneFormat Tajikistan =
        new("TJ", "+992 — Таджикистан (TJ)", "992", "(##) ###-##-##");

    public static readonly PhoneFormat Uzbekistan =
        new("UZ", "+998 — Узбекистан (UZ)", "998", "(##) ###-##-##");

    public static IReadOnlyList<PhoneFormat> All { get; } =
        Array.AsReadOnly(new[] { RussiaKazakhstan, Tajikistan, Uzbekistan });

    /// <summary>Чем становится касса, где настройка не задана. Он же ответ на
    /// настройку, оставшуюся от удалённой записи каталога.</summary>
    public static PhoneFormat Default => RussiaKazakhstan;

    /// <summary>Единственное место, где Id превращается в формат. Функцией, а не
    /// веткой на месте использования: правило «пусто или незнакомо — значит RU»
    /// должно быть одно и проверяться тестом без файловой системы.</summary>
    public static PhoneFormat Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var format in All)
            {
                if (string.Equals(format.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }
        }

        return Default;
    }
}
