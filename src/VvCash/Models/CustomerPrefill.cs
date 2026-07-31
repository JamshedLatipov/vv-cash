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

    /// <summary>Ровно digitCount цифр или пусто. CustomerRegistrationViewModel.SubmitAsync
    /// отправляет телефон только при полной длине по формату кассы и сам
    /// приклеивает код страны, поэтому хранить здесь что-то другое
    /// бессмысленно.</summary>
    public string PhoneNumber { get; init; } = string.Empty;

    public static readonly CustomerPrefill Empty = new();

    /// <param name="digitCount">Сколько цифр в полном национальном номере на
    /// этой кассе — из PhoneFormat. Порог «это телефон» и длина среза берутся
    /// отсюда: на девятизначной кассе десятка отправляла бы полный местный номер
    /// в имя.</param>
    public static CustomerPrefill FromSearchQuery(string? query, int digitCount)
    {
        if (string.IsNullOrWhiteSpace(query)) return Empty;

        // Порог по числу цифр, а не «строка состоит только из цифр»: кассир
        // набирает телефон и со скобками с дефисами — «+7 (900) 123-45-67» на
        // десятизначной кассе, «+992 (90) 123-45-67» на девятизначной. Сколько
        // именно цифр считать номером, решает digitCount, а не этот пример.
        // Берутся последние, чтобы код страны в начале не сдвигал номер.
        var digits = new string(query.Where(char.IsDigit).ToArray());
        if (digits.Length >= digitCount)
        {
            return new CustomerPrefill { PhoneNumber = digits[^digitCount..] };
        }

        // null как разделитель — это split по любому пробельному символу.
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return new CustomerPrefill
        {
            FirstName = words[0],
            LastName = string.Join(' ', words.Skip(1)),
        };
    }
}
