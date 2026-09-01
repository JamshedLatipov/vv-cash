using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Data;

public interface IOfflineStorageService
{
    Task SaveProductsAsync(IEnumerable<Product> products);
    Task<IEnumerable<Product>> GetAllProductsAsync();
    Task<IEnumerable<Product>> GetProductsByCategoryAsync(string categoryId);
    Task<Product?> GetProductByBarcodeAsync(string barcode);

    /// <summary>Products whose name, article or barcode contain <paramref name="query"/>,
    /// matched case-insensitively. In the database rather than over an in-memory copy of
    /// the catalog: this runs on every keystroke in the POS search box, and loading and
    /// materialising every product per character is what it used to cost.</summary>
    Task<IEnumerable<Product>> SearchProductsAsync(string query);

    Task SaveCategoriesAsync(IEnumerable<Category> categories);
    Task<IEnumerable<Category>> GetCategoriesAsync();

    Task SaveQuickAccessCategoriesAsync(IEnumerable<Category> categories);
    Task<IEnumerable<Category>> GetQuickAccessCategoriesAsync();

    // Auto-applied promotions, cached so carts can be priced while offline.
    Task SavePromotionsAsync(IEnumerable<Promotion> promotions);
    Task<IEnumerable<Promotion>> GetPromotionsAsync();
    Task ClearPromotionsAsync();

    // Store money rounding, so offline pricing rounds the way the server does.
    Task SaveMoneyPolicyAsync(MoneyPolicy policy);
    Task<MoneyPolicy> GetMoneyPolicyAsync();

    // Which register functions are switched on, so the POS screen knows what to
    // show before the first sync of the day completes.
    Task SaveCashFeaturesAsync(CashFeatures features);
    Task<CashFeatures> GetCashFeaturesAsync();

    /// <summary>Сырое значение опции receipt_template, как его вернул сервер.
    /// Именно сырое, а не разобранное: разбор — дело ReceiptTemplate.Parse, и
    /// держать его в двух местах незачем.
    ///
    /// Пустая строка неотличима от «шаблон никогда не приезжал» — сохранённое
    /// пустое и никогда не сохранённое читаются одинаково. Для шаблона это не
    /// важно: ReceiptTemplate.Parse на обоих даёт один и тот же Default. Не
    /// переноси это допущение на логотип ниже без проверки — там разница уже
    /// существенна.
    ///
    /// Синхронно блокирует вызывающий поток на время обращения к SQLite (как и
    /// весь этот класс) — не звать с пути печати чека.</summary>
    Task SaveReceiptTemplateAsync(string raw);
    Task<string> GetReceiptTemplateAsync();

    /// <summary>Растровый логотип в base64, уже сведённый в один бит бэкофисом.
    /// Отдельно от шаблона: 7–8 КБ не должны ездить внутри каждого шаблона.
    ///
    /// Пустая строка здесь — не только «логотип никогда не приезжал»: бэкофис
    /// может стереть ранее заданный логотип, и сервер тогда законно присылает
    /// пустое значение — тот же случай, что для акций в SyncService.SyncPromotionsAsync,
    /// где пустое тело чистит кэш, а не оставляет прежнее значение висеть навсегда.
    /// SaveReceiptLogoAsync обязана сохранить пустую строку как есть: молча
    /// пропустить сохранение здесь значит оставить на чеке логотип, который в
    /// бэкофисе уже удалили.
    ///
    /// Синхронно блокирует вызывающий поток на время обращения к SQLite (как и
    /// весь этот класс) — не звать с пути печати чека.</summary>
    Task SaveReceiptLogoAsync(string base64);
    Task<string> GetReceiptLogoAsync();

    Task SetLastSyncVersionAsync(int version);
    Task SaveUnsyncedDocumentAsync(string hash, string payload);
    Task<IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>> GetUnsyncedDocumentsAsync();
    Task DeleteUnsyncedDocumentAsync(string hash);

    /// <summary>Takes a queued document out of the retry rotation without discarding it,
    /// for the one case where retrying is pointless: the server answered, on its merits,
    /// that it will not accept this document. Retrying that forever is what made the
    /// unsynced badge a permanent fixture on some registers — the count never dropped,
    /// and a genuinely queued sale behind it was indistinguishable from the stuck one.
    /// The row stays in the database with its reason so the back office can still read
    /// what the register tried to book; it just stops being resent.</summary>
    Task MarkDocumentRejectedAsync(string hash, string reason);
    Task<int> GetLastSyncVersionAsync();

    Task ClearCategoriesAsync();
    Task ClearProductsAsync();

    /// <summary>Applies one complete reconciliation walk: products absent from
    /// <paramref name="remains"/> are deleted, the rest have their stock stamped.
    ///
    /// Only ever call this with the result of a walk that finished. A partial map means
    /// a partial delete, and a partial delete of the catalogue is worse than a stale
    /// one. An empty map is the same danger taken to its limit — nothing here can tell
    /// a warehouse that truly has no stock left apart from a walk that never finished, so
    /// it is refused with <see cref="ArgumentException"/> rather than trusted; a caller
    /// cannot reach the empty-catalogue outcome by accident.</summary>
    Task ApplyRemainsAsync(IReadOnlyDictionary<string, decimal> remains);

    // Parked sales (отложенные чеки)
    Task SaveParkedSaleAsync(ParkedSale sale);
    Task<IEnumerable<ParkedSale>> GetParkedSalesAsync();
    Task<ParkedSale?> GetParkedSaleAsync(string id);
    Task DeleteParkedSaleAsync(string id);

    // Seller roster cache (продавцы кассы)
    Task SaveSellersAsync(IEnumerable<SellerInfo> sellers);
    Task<IEnumerable<SellerInfo>> GetSellersAsync();

    Task InitializeAsync();
}
