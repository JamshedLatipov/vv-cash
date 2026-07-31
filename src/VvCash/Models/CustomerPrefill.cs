using System;
using System.Linq;

namespace VvCash.Models;

/// <summary>Что строка поиска отдаёт пустой форме регистрации, когда кассир
/// искал клиента, не нашёл и жмёт «Создать». Тип намеренно ничего не знает ни
/// о view model, ни о формате API: единственное здесь решение — что считать
/// телефоном, а что именем, — должно проверяться без Avalonia и без сети.</summary>
public sealed class CustomerPrefill
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;

    /// <summary>Ровно десять цифр или пусто. CustomerRegistrationViewModel.SubmitAsync
    /// отправляет телефон только при длине 10 и сам приклеивает код страны,
    /// поэтому хранить здесь что-то другое бессмысленно.</summary>
    public string PhoneNumber { get; init; } = string.Empty;

    public static readonly CustomerPrefill Empty = new();

    public static CustomerPrefill FromSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Empty;

        // Порог по числу цифр, а не «строка состоит только из цифр»: кассир
        // набирает телефон и как «+7 (900) 123-45-67». Берутся последние десять,
        // чтобы ведущие 7/8 не сдвигали номер.
        var digits = new string(query.Where(char.IsDigit).ToArray());
        if (digits.Length >= 10)
        {
            return new CustomerPrefill { PhoneNumber = digits[^10..] };
        }

        // null как разделитель — это split по любому пробельному символу.
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Empty;

        return new CustomerPrefill
        {
            FirstName = words[0],
            LastName = string.Join(' ', words.Skip(1)),
        };
    }
}
