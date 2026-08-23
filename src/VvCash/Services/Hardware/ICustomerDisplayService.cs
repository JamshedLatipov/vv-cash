using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

/// <summary>Витрина покупателя.
///
/// Возвращает bool, а не голый Task: без него кнопка «Проверить дисплей» на экране
/// настроек физически не может отчитаться, а служба остаётся при той же болезни
/// «рапортует успех», которую батч чинит у USB-печати.
///
/// Пять вызовов из PosViewModel результат намеренно не ждут (`_ = …`): витрина не
/// должна ни задерживать продажу, ни ронять её, если у неё отвалился COM-порт.
/// bool заведён ради единственного места, где на него есть кому смотреть.</summary>
public interface ICustomerDisplayService
{
    Task<bool> ShowLineAsync(string line1, string line2);
    Task<bool> ShowItemAsync(string name, decimal price);
    Task<bool> ShowTotalAsync(decimal total);
    Task<bool> ClearAsync();
}
