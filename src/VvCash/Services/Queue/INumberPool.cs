using System.Threading.Tasks;

namespace VvCash.Services.Queue;

public interface INumberPool
{
    /// <summary>Следующий номер для клиента. Никого не спрашивает по сети — на
    /// этом стоит вся оффлайн-устойчивость очереди.</summary>
    Task<int> IssueAsync();

    /// <summary>Возвращает номер в оборот. Раньше кулдауна он всё равно не
    /// выдастся — см. NumberPool.CooldownIssues. Повторный вызов для уже
    /// свободного номера — no-op: он не отодвигает окно кулдауна заново.
    /// Это не деталь реализации, а необходимое условие для вызывающих вроде
    /// QueueClient.FlushAsync, которым сервер называет одни и те же закрытые
    /// заказы на каждом опросе.</summary>
    Task ReleaseAsync(int number);
}
