using System.Threading.Tasks;
using VvCash.Models.Receipt;
using VvCash.Services.Data;

namespace VvCash.Services;

/// <summary>Устроен как CashFeatureService и по той же причине: касса
/// мид-запуска обязана отрисовать рабочий экран и напечатать чек, а не бросить.</summary>
public class ReceiptTemplateService : IReceiptTemplateService
{
    private readonly IOfflineStorageService _storage;

    public ReceiptTemplateService(IOfflineStorageService storage) => _storage = storage;

    public ReceiptTemplate Current { get; private set; } = ReceiptTemplate.Default;

    public string Logo { get; private set; } = string.Empty;

    public async Task RefreshAsync()
    {
        Current = ReceiptTemplate.Parse(await _storage.GetReceiptTemplateAsync());
        Logo = await _storage.GetReceiptLogoAsync();
    }
}
