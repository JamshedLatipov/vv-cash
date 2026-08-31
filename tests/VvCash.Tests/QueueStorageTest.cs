using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

public class QueueStorageTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static QueueOrder Order(Guid? id = null, int number = 305, DateTime? createdAt = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Number = number,
        TillIndex = 0,
        State = QueueOrderState.New,
        CreatedAt = createdAt ?? new DateTime(2026, 8, 31, 10, 0, 0),
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "2 pcs" } }
    };

    [Fact]
    public async Task InitializeIsIdempotent()
    {
        var path = TempDb();
        var storage = new QueueStorage(path);

        await storage.InitializeAsync();
        await storage.InitializeAsync();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task StateSurvivesReopening()
    {
        var path = TempDb();
        var first = new QueueStorage(path);
        await first.InitializeAsync();
        await first.SetStateAsync("Day", "2026-08-31");

        var second = new QueueStorage(path);
        await second.InitializeAsync();

        Assert.Equal("2026-08-31", await second.GetStateAsync("Day"));
    }

    [Fact]
    public async Task MissingStateReadsAsNull()
    {
        var storage = new QueueStorage(TempDb());
        await storage.InitializeAsync();

        Assert.Null(await storage.GetStateAsync("Day"));
    }

    [Fact]
    public async Task ANewStorageListsNoOrders()
    {
        var storage = new QueueStorage(TempDb());
        await storage.InitializeAsync();

        Assert.Empty(await storage.GetOrdersAsync());
    }

    [Fact]
    public async Task ASavedOrderAppearsInTheListing()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order();

        await storage.SaveOrderAsync(order);

        var stored = Assert.Single(await storage.GetOrdersAsync());
        Assert.Equal(order.Id, stored.Id);
        Assert.Equal(order.Number, stored.Number);
        Assert.Equal(QueueOrderState.New, stored.State);
        Assert.Equal(order.Lines.Single().Name, stored.Lines.Single().Name);
    }

    [Fact]
    public async Task SavingTheSameOrderTwiceDoesNotDuplicateIt()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order();

        await storage.SaveOrderAsync(order);
        await storage.SaveOrderAsync(order);

        Assert.Single(await storage.GetOrdersAsync());
    }

    /// <summary>"o" на запись и RoundtripKind на чтение — та же пара, что уже
    /// использует OfflineStorageService для ParkedSales.CreatedAt (см. докстринг
    /// QueueStorage.ParseDate). Culture меняется намеренно: ru-RU расставляет
    /// день перед месяцем и запятую вместо точки в дробной секунде — ровно то,
    /// на чём ломается культурно-зависимый парсинг, а "o"/RoundtripKind обязаны
    /// его игнорировать.</summary>
    [Fact]
    public async Task TimestampsRoundTripExactlyRegardlessOfCulture()
    {
        var storage = new QueueStorage(TempDb());
        var createdAt = new DateTime(2026, 8, 31, 23, 59, 59, 987, DateTimeKind.Local);
        var order = Order(createdAt: createdAt);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            await storage.SaveOrderAsync(order);
            var stored = (await storage.GetOrdersAsync()).Single(o => o.Id == order.Id);

            Assert.Equal(createdAt, stored.CreatedAt);
            Assert.Equal(createdAt.Kind, stored.CreatedAt.Kind);
            Assert.Equal(createdAt.Ticks, stored.CreatedAt.Ticks);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
