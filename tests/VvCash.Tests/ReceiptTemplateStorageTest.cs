using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VvCash.Models.Receipt;
using VvCash.Services.Data;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateStorageTest
{
    [Fact]
    public async Task RawTemplate_RoundTrips()
    {
        var storage = await NewStorage();
        var json = """{"version":1,"width":42,"blocks":[]}""";

        await storage.SaveReceiptTemplateAsync(json);

        Assert.Equal(json, await storage.GetReceiptTemplateAsync());
    }

    [Fact]
    public async Task RawTemplate_IsEmpty_WhenNothingWasEverSynced()
    {
        var storage = await NewStorage();

        Assert.True(string.IsNullOrEmpty(await storage.GetReceiptTemplateAsync()));
    }

    [Fact]
    public async Task ACorruptCachedTemplate_ParsesToTheDefault_RatherThanThrowing()
    {
        // Опция receiptTemplate засеяна в 2019 и шесть лет рендерилась текстовым
        // полем — в configs.val у живого тенанта может лежать что угодно.
        var storage = await NewStorage();
        await storage.SaveReceiptTemplateAsync("{это не json");

        // Сравнение сериализованного JSON, а НЕ Assert.Same: ReceiptTemplate.Default
        // это фабрика (`=> new()`), новый объект на каждое обращение, поэтому
        // ссылочная тождественность здесь никогда не выполнится. Тот же приём
        // используют тесты ReceiptTemplateTest.
        var parsed = ReceiptTemplate.Parse(await storage.GetReceiptTemplateAsync());

        Assert.Equal(
            JsonSerializer.Serialize(ReceiptTemplate.Default, ReceiptTemplate.Options),
            JsonSerializer.Serialize(parsed, ReceiptTemplate.Options));
    }

    [Fact]
    public async Task Logo_RoundTrips()
    {
        var storage = await NewStorage();

        await storage.SaveReceiptLogoAsync("AAECAw==");

        Assert.Equal("AAECAw==", await storage.GetReceiptLogoAsync());
    }

    /// <summary>InitializeAsync обязателен: именно он создаёт таблицу Settings,
    /// из которой всё это читается. Без него тест падает не про то.</summary>
    private static async Task<OfflineStorageService> NewStorage()
    {
        var storage = new OfflineStorageService(
            Path.Combine(Path.GetTempPath(), $"vvcash-receipt-{System.Guid.NewGuid():N}.db"));
        await storage.InitializeAsync();
        return storage;
    }
}
