using System;
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

    /// <summary>Читает шаблон и логотип из кэша. В отличие от CashFeatureService,
    /// который полагается только на порядок вызовов (InitializeAsync раньше
    /// RefreshAsync — см. PosViewModel.InitializeAsync), этот метод сам обязан
    /// пережить нарушение этого порядка: он читается ещё и на старте App.axaml.cs,
    /// синхронно, до логина кассира и до того, как что-либо гарантированно создало
    /// таблицу Settings. Без этой защиты чтение кэша на свежем профиле бросает
    /// SqliteException("no such table: Settings"), необработанное исключение
    /// убивает процесс раньше, чем окно логина успевает открыться, и запасной путь
    /// "Current — Default, пока не обновится" из документации выше не успевает
    /// сработать вовсе. Любая беда здесь — тот же принцип, что и в SyncService:
    /// оставить дефолт и написать в лог, а не уронить кассу.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            Current = ReceiptTemplate.Parse(await _storage.GetReceiptTemplateAsync());
            Logo = await _storage.GetReceiptLogoAsync();
        }
        catch (Exception ex)
        {
            Current = ReceiptTemplate.Default;
            Logo = string.Empty;
            Console.WriteLine($"[ReceiptTemplateService] refresh error: {ex.Message}, using default template");
        }
    }
}
