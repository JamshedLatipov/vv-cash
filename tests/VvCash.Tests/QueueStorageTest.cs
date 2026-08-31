using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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

        Assert.Empty(await storage.GetLiveOrdersAsync());
    }

    [Fact]
    public async Task ASavedOrderAppearsInTheListing()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order();

        await storage.SaveOrderAsync(order, order.CreatedAt);

        var stored = Assert.Single(await storage.GetLiveOrdersAsync());
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

        await storage.SaveOrderAsync(order, order.CreatedAt);
        await storage.SaveOrderAsync(order, order.CreatedAt);

        Assert.Single(await storage.GetLiveOrdersAsync());
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

            await storage.SaveOrderAsync(order, order.CreatedAt);
            var stored = (await storage.GetLiveOrdersAsync()).Single(o => o.Id == order.Id);

            Assert.Equal(createdAt, stored.CreatedAt);
            Assert.Equal(createdAt.Kind, stored.CreatedAt.Kind);
            Assert.Equal(createdAt.Ticks, stored.CreatedAt.Ticks);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>ON CONFLICT DO NOTHING, не UPDATE — доказательство от противного.
    /// Клиент досылает буфер, не зная, что заказ уже успел продвинуться на
    /// кухне; повторно пришедшая копия (та же самая, State = New) не должна
    /// откатить его обратно. Под UPDATE вместо DO NOTHING этот тест красный.</summary>
    [Fact]
    public async Task AResentCopyDoesNotResetAnOrderThatAlreadyMovedOn()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order();
        await storage.SaveOrderAsync(order, order.CreatedAt);

        var advanced = await storage.GetOrderAsync(order.Id);
        advanced!.State = QueueOrderState.InProgress;
        await storage.UpdateOrderStateAsync(advanced);

        // Та же самая (по Id) стартовая копия, будто клиент досылает буфер заново.
        await storage.SaveOrderAsync(order, order.CreatedAt);

        var stored = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.InProgress, stored!.State);
    }

    [Fact]
    public async Task AnUnknownOrderIdReadsAsNull()
    {
        var storage = new QueueStorage(TempDb());
        await storage.InitializeAsync();

        Assert.Null(await storage.GetOrderAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateOrderStateStampsReadyAndClosedIndependently()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order();
        await storage.SaveOrderAsync(order, order.CreatedAt);

        order.State = QueueOrderState.InProgress;
        await storage.UpdateOrderStateAsync(order);

        var readyAt = new DateTime(2026, 8, 31, 10, 5, 0);
        order.State = QueueOrderState.Ready;
        order.ReadyAt = readyAt;
        await storage.UpdateOrderStateAsync(order);

        var stored = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.Ready, stored!.State);
        Assert.Equal(readyAt, stored.ReadyAt);
        Assert.Null(stored.ClosedAt);
    }

    /// <summary>Critical 2's fix adds NumberPool.IssuedFor, which a queue.db already
    /// sitting on a developer's (or a shop's) machine predates. Builds a database in
    /// exactly that pre-migration shape — NumberPool without the column, one row already
    /// issued — and checks that InitializeAsync adds the column without throwing, keeps
    /// the existing row intact, and that NumberPool itself (issue, then release by the
    /// same order id) works normally afterwards. Same idiom as
    /// OfflineStorageServiceTest.InitializeAsync_UpgradingFromRealColumns_RebuildsAsTextAndKeepsRows,
    /// which QueueStorage's own AddColumnIfMissingAsync now follows too.</summary>
    [Fact]
    public async Task ANumberPoolFromBeforeIssuedForStillOpensAndMigrates()
    {
        var dbPath = TempDb();
        try
        {
            await SeedPreIssuedForDatabaseAsync(dbPath);

            var storage = new QueueStorage(dbPath);
            await storage.InitializeAsync();

            using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();

            // The column exists now...
            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT type FROM pragma_table_info('NumberPool') WHERE name = 'IssuedFor';";
                Assert.Equal("TEXT", (await cmd.ExecuteScalarAsync()) as string);
            }

            // ...and the pre-migration row survived untouched.
            using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT Position, IssuedSeq, ReleasedAtSeq FROM NumberPool WHERE Number = 305;";
                using var rd = await cmd.ExecuteReaderAsync();
                Assert.True(await rd.ReadAsync());
                Assert.Equal(0, rd.GetInt32(0));
                Assert.Equal(3, rd.GetInt32(1));
                Assert.True(rd.IsDBNull(2));
            }

            // NumberPool built on the same file keeps working: it can issue a fresh
            // number and later release that same number by the order id it issued it
            // to, exercising exactly the column this migration added.
            var pool = new NumberPool(storage, tillIndex: 0, "secret", () => new DateTime(2026, 8, 31, 10, 0, 0));
            var orderId = Guid.NewGuid();
            var number = await pool.IssueAsync(orderId);
            await pool.ReleaseAsync(number, orderId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    /// <summary>Raw schema exactly as QueueStorage created it before IssuedFor existed —
    /// no IssuedFor column at all — with one number already issued (IssuedSeq = 3, like a
    /// till mid-shift) so the migration runs against a table that already has data, not
    /// an empty one.</summary>
    private static async Task SeedPreIssuedForDatabaseAsync(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE NumberPool (
                Number INTEGER PRIMARY KEY,
                Position INTEGER NOT NULL,
                IssuedSeq INTEGER,
                ReleasedAtSeq INTEGER
            );
            INSERT INTO NumberPool (Number, Position, IssuedSeq, ReleasedAtSeq)
                VALUES (305, 0, 3, NULL);
            CREATE TABLE QueueState (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );
        ";
        await command.ExecuteNonQueryAsync();
    }
}
