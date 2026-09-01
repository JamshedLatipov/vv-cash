using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

/// <summary>Витрина покупателя.
///
/// Возвращает bool, а не голый Task: без него кнопка «Проверить дисплей» на экране
/// настроек физически не может отчитаться, а служба остаётся при той же болезни
/// «рапортует успех», которую батч чинит у USB-печати.
///
/// Вызовы из PosViewModel результат намеренно не ждут (`_ = …`): витрина не должна ни
/// задерживать продажу, ни ронять её, если у неё отвалился COM-порт. bool заведён ради
/// единственного места, где на него есть кому смотреть.</summary>
public interface ICustomerDisplayService
{
    Task<bool> ShowLineAsync(string line1, string line2);

    /// <summary>Кадр «что пробили и сколько всего»: <paramref name="name"/> в верхней
    /// строке, <paramref name="total"/> — в нижней.
    ///
    /// Второй параметр — итог по чеку, а НЕ цена этого товара, и это не мелочь именования.
    /// Здесь стояла цена, а итог слался отдельным кадром, который затирался этим же —
    /// покупатель не видел суммы к оплате никогда (см. PosViewModel.PushToCustomerDisplay).
    /// Вернуть сюда product.Price — значит вернуть тот дефект целиком, причём тихо: кадр
    /// продолжит уходить, размер и разметка не изменятся.</summary>
    Task<bool> ShowItemAsync(string name, decimal total);

    Task<bool> ShowTotalAsync(decimal total);
    Task<bool> ClearAsync();
}
