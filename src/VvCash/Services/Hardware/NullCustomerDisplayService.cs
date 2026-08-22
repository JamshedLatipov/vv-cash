using System.Threading.Tasks;

namespace VvCash.Services.Hardware;

/// <summary>Касса без VFD. Это нормальное состояние, а не отсутствие реализации:
/// дисплей покупателя есть далеко не на каждой точке.
///
/// Пришёл на смену MockCustomerDisplayService, который был зарегистрирован боевым
/// и сорил каждой продажей в консоль. Имя врало: «mock» обещает подмену на время
/// тестов, а это рабочее поведение ненастроенной кассы.
///
/// Сигнатуры временно возвращают голый Task: ICustomerDisplayService меняется на
/// Task&lt;bool&gt; следующим коммитом этого же батча, и эта заглушка меняется вместе
/// с ним. Здесь, до правки интерфейса, Task&lt;bool&gt; не собрался бы.</summary>
public class NullCustomerDisplayService : ICustomerDisplayService
{
    public Task ShowLineAsync(string line1, string line2) => Task.CompletedTask;
    public Task ShowItemAsync(string name, decimal price) => Task.CompletedTask;
    public Task ShowTotalAsync(decimal total) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
}
