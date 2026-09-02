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

    /// <summary>Шаблон и логотип одной парой, за одно чтение внутреннего
    /// снимка. Единственный безопасный способ получить оба значения вместе:
    /// `(Current, Logo)` — это ДВА отдельных обращения к снимку, и обновление,
    /// уложившееся между ними, отдало бы половинки разных поколений — шаблон
    /// одного, логотип другого. Печать — единственный настоящий читатель,
    /// которому оба значения нужны сразу, — обязана брать их отсюда, а не
    /// собирать кортеж из двух свойств выше по стеку вызовов.</summary>
    (ReceiptTemplate Template, string Logo) CurrentTemplateAndLogo { get; }

    Task RefreshAsync();
}
