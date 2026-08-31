using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Task 25: перевод дня (QueueStorage.CloseStaleOrdersAsync) и счётчик
/// исходящего буфера (GetOutboxCountAsync). Один файл на оба — обе проверки
/// принадлежат одному и тому же куску задачи: три хвоста, без которых очередь
/// молча ведёт себя не так, как должна.</summary>
public class QueueDayRolloverTest
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static QueueOrder Order(
        DateTime createdAt,
        QueueOrderState state = QueueOrderState.New,
        int number = 305,
        DateTime? closedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Number = number,
        TillIndex = 0,
        State = state,
        CreatedAt = createdAt,
        ClosedAt = closedAt,
        Lines = new List<QueueOrderLine> { new() { Name = "Coffee", Quantity = "2 pcs" } }
    };

    [Fact]
    public async Task YesterdaysUnfinishedOrdersClose()
    {
        var storage = new QueueStorage(TempDb());
        var isNew = Order(new DateTime(2026, 8, 30, 9, 0, 0), QueueOrderState.New);
        var inProgress = Order(new DateTime(2026, 8, 30, 10, 0, 0), QueueOrderState.InProgress, number: 306);
        var ready = Order(new DateTime(2026, 8, 30, 11, 0, 0), QueueOrderState.Ready, number: 307);
        await storage.SaveOrderAsync(isNew);
        await storage.SaveOrderAsync(inProgress);
        await storage.SaveOrderAsync(ready);

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        foreach (var id in new[] { isNew.Id, inProgress.Id, ready.Id })
        {
            var reloaded = await storage.GetOrderAsync(id);
            Assert.Equal(QueueOrderState.Closed, reloaded!.State);
            Assert.NotNull(reloaded.ClosedAt);
        }
    }

    [Fact]
    public async Task TodaysOrdersAreUntouched()
    {
        var storage = new QueueStorage(TempDb());
        // Ранним утром того же календарного дня, что и today ниже — не
        // "прошло меньше 24 часов", а буквально тот же день.
        var order = Order(new DateTime(2026, 8, 31, 6, 0, 0), QueueOrderState.New);
        await storage.SaveOrderAsync(order);

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.New, reloaded!.State);
        Assert.Null(reloaded.ClosedAt);
    }

    /// <summary>Closed и Cancelled — разные исходы, и отчёт когда-нибудь
    /// спросит про разницу (см. докстринг IQueueStorage.CloseStaleOrdersAsync).
    /// Заказ, отменённый вчера, не должен молча стать «закрытым по
    /// расписанию» ни в State, ни в ClosedAt.</summary>
    [Fact]
    public async Task AnAlreadyCancelledOrderFromYesterdayStaysCancelled()
    {
        var storage = new QueueStorage(TempDb());
        var cancelledAt = new DateTime(2026, 8, 30, 12, 0, 0);
        var order = Order(new DateTime(2026, 8, 30, 9, 0, 0), QueueOrderState.Cancelled, closedAt: cancelledAt);
        await storage.SaveOrderAsync(order);

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.Cancelled, reloaded!.State);
        Assert.Equal(cancelledAt, reloaded.ClosedAt);
    }

    /// <summary>Календарный день, а не "прошло 24 часа": между CreatedAt и
    /// today ниже — 31 минута, не сутки. Заказ, пробитый в 23:59, обязан
    /// закрыться к утру следующего дня, а не через ровно 24 часа от момента
    /// пробития. Проверяет ровно тот сценарий, который докстринг
    /// IQueueStorage.CloseStaleOrdersAsync называет явно.</summary>
    [Fact]
    public async Task AnOrderRungUpJustBeforeMidnightClosesInTheMorningNotADayLater()
    {
        var storage = new QueueStorage(TempDb());
        var order = Order(new DateTime(2026, 8, 30, 23, 59, 0), QueueOrderState.New);
        await storage.SaveOrderAsync(order);

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 0, 30, 0));

        var reloaded = await storage.GetOrderAsync(order.Id);
        Assert.Equal(QueueOrderState.Closed, reloaded!.State);
    }

    /// <summary>Разбирает опасение из ревью задачи: "ISO-строка и SQL-функция
    /// date() не обязательно согласны". Строка на диске здесь собрана вручную,
    /// в обход SaveOrderAsync, но в точности в том формате, что реально пишет
    /// BindOrder на кассе — DateTime.Now (Kind.Local) через ToString("o"), со
    /// встроенным оффсетом зоны кассы (вычислен через TimeZoneInfo.Local, а не
    /// зашит числом, — тест остаётся верным на любой машине, где его
    /// запустят). Гипотетический WHERE date(CreatedAt) &lt; date($Today) сперва
    /// свёл бы эту строку к UTC по её оффсету и лишь потом взял календарную
    /// дату — для момента за 15 минут до местной полуночи это увело бы день
    /// на один вперёд, и заказ не закрылся бы вовсе. CloseStaleOrdersAsync
    /// так не делает: читает CreatedAt тем же DateTime.Parse(...,
    /// DateTimeStyles.RoundtripKind), которым ReadOrder уже читает его для
    /// GetOrdersAsync/GetOrderAsync, и сравнивает .Date без какой-либо
    /// повторной конвертации через UTC.</summary>
    [Fact]
    public async Task AnOrderStoredWithTheTillsRealOffsetStillClosesAcrossMidnight()
    {
        var storage = new QueueStorage(TempDb());
        await storage.InitializeAsync();

        var justBeforeMidnight = new DateTime(2026, 8, 30, 23, 45, 0);
        var offset = TimeZoneInfo.Local.GetUtcOffset(justBeforeMidnight);
        var createdAt = new DateTimeOffset(justBeforeMidnight, offset)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");

        var id = Guid.NewGuid();
        using (var connection = new SqliteConnection(storage.ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO QueueOrders
                    (Id, Number, TillIndex, State, CreatedAt, ReadyAt, ClosedAt, SaleDocumentNumber, Lines)
                VALUES
                    ($Id, 308, 0, 'New', $CreatedAt, NULL, NULL, '', '[]');
            ";
            command.Parameters.AddWithValue("$Id", id.ToString());
            command.Parameters.AddWithValue("$CreatedAt", createdAt);
            await command.ExecuteNonQueryAsync();
        }

        await storage.CloseStaleOrdersAsync(new DateTime(2026, 8, 31, 9, 0, 0));

        var reloaded = await storage.GetOrderAsync(id);
        Assert.Equal(QueueOrderState.Closed, reloaded!.State);
    }

    [Fact]
    public async Task OutboxCountReportsWhatTheBufferHolds()
    {
        var storage = new QueueStorage(TempDb());

        Assert.Equal(0, await storage.GetOutboxCountAsync("Order"));

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await storage.SaveOutboxAsync(first, "Order", "{}");
        await storage.SaveOutboxAsync(second, "Order", "{}");
        Assert.Equal(2, await storage.GetOutboxCountAsync("Order"));

        // Отклонённая строка больше не в ротации отправки — GetOutboxAsync её
        // уже не отдаёт (см. его докстринг), счётчик не должен отдавать её тоже.
        await storage.MarkOutboxRejectedAsync(first, "test");
        Assert.Equal(1, await storage.GetOutboxCountAsync("Order"));

        await storage.DeleteOutboxAsync(second);
        Assert.Equal(0, await storage.GetOutboxCountAsync("Order"));

        // Другой Kind — другой счёт: буфер общий, но заказы не должны
        // считаться в счётчике смен состояния (будущий Kind) и наоборот.
        await storage.SaveOutboxAsync(Guid.NewGuid(), "State", "{}");
        Assert.Equal(0, await storage.GetOutboxCountAsync("Order"));
        Assert.Equal(1, await storage.GetOutboxCountAsync("State"));
    }
}
