using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

/// <summary>Касса без VFD. Это нормальное состояние, а не отсутствие реализации:
/// дисплей покупателя есть далеко не на каждой точке.
///
/// Пришёл на смену MockCustomerDisplayService, который был зарегистрирован боевым
/// и сорил каждой продажей в консоль. Имя врало: «mock» обещает подмену на время
/// тестов, а это рабочее поведение ненастроенной кассы.
///
/// Возвращает true: «показывать нечего» — не отказ, и кнопка проверки дисплея на
/// такой кассе не должна показывать ошибку.</summary>
public class NullCustomerDisplayService : ICustomerDisplayService
{
    public Task<bool> ShowLineAsync(string line1, string line2) => Task.FromResult(true);
    public Task<bool> ShowItemAsync(string name, decimal price) => Task.FromResult(true);
    public Task<bool> ShowTotalAsync(decimal total) => Task.FromResult(true);
    public Task<bool> ClearAsync() => Task.FromResult(true);
}
