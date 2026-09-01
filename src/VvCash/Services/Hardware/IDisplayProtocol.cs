using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Один диалект табло покупателя: превращает смысл в байты и ничего не знает
/// о портах.
///
/// Отдельно от VfdDisplayService, потому что порт на кассе один, а диалектов много.
/// Транспорт с его очередью, таймаутом и catch, который держит хвост очереди живым,
/// остаётся в одном экземпляре; меняется только то, какие байты в него кладут.
///
/// Все методы чистые, поэтому проверяются без открытия порта — тем же приёмом, что
/// раньше давали статические Build*Frame.</summary>
public interface IDisplayProtocol
{
    /// <summary>То, что ложится в настройки. Хранится он, а не DisplayName: правка
    /// подписи в интерфейсе не должна ломать настроенную кассу.</summary>
    string Id { get; }

    /// <summary>Не переводится и живёт в коде — как у EscPosCodePage: название
    /// протокола опознаётся независимо от письменности.</summary>
    string DisplayName { get; }

    byte[] BuildLine(string line1, string line2, EscPosCodePage codePage);

    /// <summary>Второй параметр — итог по чеку, а не цена товара. См. одноимённое
    /// предупреждение в ICustomerDisplayService.ShowItemAsync.</summary>
    byte[] BuildItem(string name, decimal total, EscPosCodePage codePage);

    byte[] BuildTotal(decimal total, EscPosCodePage codePage);

    byte[] BuildClear(EscPosCodePage codePage);

    /// <summary>Кадр автоподбора: номер комбинации и ничего больше.
    ///
    /// Без кодовой страницы намеренно. Цифры одинаковы во всех однобайтовых таблицах,
    /// а пробник обязан читаться в том числе тогда, когда таблица выбрана неверно —
    /// иначе он проверяет заодно и её.</summary>
    byte[] BuildProbe(int number);
}
