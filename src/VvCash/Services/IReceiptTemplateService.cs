using System.Threading.Tasks;
using VvCash.Models.Receipt;

namespace VvCash.Services;

public interface IReceiptTemplateService
{
    /// <summary>Действующий шаблон. До первого RefreshAsync — Default: касса на
    /// старте обязана уметь печатать, а не ждать сети.</summary>
    ReceiptTemplate Current { get; }

    /// <summary>Растровый логотип в base64, пусто — его нет.</summary>
    string Logo { get; }

    Task RefreshAsync();
}
