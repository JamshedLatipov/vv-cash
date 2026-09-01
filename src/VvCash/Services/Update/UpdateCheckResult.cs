namespace VvCash.Services.Update;

/// <summary>Чем закончилась проверка обновления.
///
/// Раньше <see cref="IUpdateService.CheckAsync"/> отдавала просто <c>UpdateInfo?</c>, и
/// в комментарии стояло, что вызывающему нечего делать с разницей между «нет новой
/// версии» и «проверка не удалась». Для часового таймера это было верно: он всё равно
/// повторит через час и молчит в обоих случаях.
///
/// С появлением кнопки ручной проверки это перестало быть верным. Кассир нажал и ждёт
/// ответа, а «обновлений нет» и «до сервера не достучались» — разные новости, которые
/// чинятся в разных местах: первая ничего не требует, вторая ведёт к сети, сертификатам
/// и адресу сервера. Один и тот же ответ на оба случая отправил бы его искать не там.</summary>
public sealed record UpdateCheckResult(UpdateInfo? Update, string? Failure)
{
    /// <summary>Сервер ответил, новее ничего нет.</summary>
    public static UpdateCheckResult UpToDate() => new(null, null);

    /// <summary>Сервер ответил, есть версия новее установленной.</summary>
    public static UpdateCheckResult Found(UpdateInfo info) => new(info, null);

    /// <summary>До ответа дело не дошло: сеть, таймаут, не тот тип содержимого,
    /// испорченный манифест. <paramref name="reason"/> показывается кассиру, поэтому
    /// он должен говорить о причине, а не о месте в коде.</summary>
    public static UpdateCheckResult Failed(string reason) => new(null, reason);

    public bool IsFailure => Failure is not null;
}
