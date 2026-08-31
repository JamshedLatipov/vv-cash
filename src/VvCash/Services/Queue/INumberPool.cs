using System.Threading.Tasks;

namespace VvCash.Services.Queue;

public interface INumberPool
{
    /// <summary>Следующий номер для клиента. Никого не спрашивает по сети — на
    /// этом стоит вся оффлайн-устойчивость очереди.</summary>
    Task<int> IssueAsync();

    /// <summary>Возвращает номер в оборот. Раньше кулдауна он всё равно не
    /// выдастся — см. NumberPool.CooldownIssues.</summary>
    Task ReleaseAsync(int number);
}
