using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Queue;
using Xunit;

namespace VvCash.Tests;

/// <summary>Fail-open: сервер лежит — номер всё равно выдан, бумага всё равно
/// вышла, заказ лёг в буфер. Продажа не встаёт никогда, это решение спеки.</summary>
public class QueueClientTest
{
    private sealed class FakeTransport : IQueueTransport
    {
        public bool Reachable { get; set; } = true;
        public List<QueueOrder> Posted { get; } = new();

        public Task<bool> PostOrderAsync(QueueOrder order)
        {
            if (!Reachable) return Task.FromResult(false);
            // Идемпотентность живёт на сервере; здесь просто копим всё, что дошло,
            // чтобы тест увидел дубль, если клиент пошлёт его дважды.
            Posted.Add(order);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<QueueOrder>> GetClosedAsync(int tillIndex)
            => Task.FromResult<IReadOnlyList<QueueOrder>>(Array.Empty<QueueOrder>());
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"vv-queue-{Path.GetRandomFileName()}.db");

    private static (QueueClient Client, FakeTransport Transport) Build(string? db = null)
    {
        var storage = new QueueStorage(db ?? TempDb());
        var pool = new NumberPool(storage, 0, "secret", () => new DateTime(2026, 8, 31, 10, 0, 0));
        var transport = new FakeTransport();
        return (new QueueClient(storage, pool, transport, tillIndex: 0,
            () => new DateTime(2026, 8, 31, 10, 0, 0)), transport);
    }

    private static SaleReceiptData Sale() => new(
        new List<CartItem> { new() { Product = new Product { Name = "Coffee", Price = 12m }, Quantity = 2m } },
        24m, 0m, 24m);

    [Fact]
    public async Task AnOrderGetsANumberAndReachesTheServer()
    {
        var (client, transport) = Build();

        var order = await client.EnqueueAsync(Sale());

        Assert.InRange(order.Number, 100, 999);
        Assert.Single(transport.Posted);
        Assert.Equal(order.Id, transport.Posted[0].Id);
    }

    [Fact]
    public async Task TheServerBeingDownStillYieldsANumber()
    {
        var (client, transport) = Build();
        transport.Reachable = false;

        var order = await client.EnqueueAsync(Sale());

        Assert.InRange(order.Number, 100, 999);
        Assert.Empty(transport.Posted);
    }

    [Fact]
    public async Task WhatCouldNotBeSentIsSentWhenTheServerReturns()
    {
        var (client, transport) = Build();
        transport.Reachable = false;
        var first = await client.EnqueueAsync(Sale());
        var second = await client.EnqueueAsync(Sale());

        transport.Reachable = true;
        await client.FlushAsync();

        Assert.Equal(2, transport.Posted.Count);
        Assert.Contains(transport.Posted, o => o.Id == first.Id);
        Assert.Contains(transport.Posted, o => o.Id == second.Id);
    }

    [Fact]
    public async Task FlushingTwiceDoesNotSendTheSameOrderTwice()
    {
        var (client, transport) = Build();
        transport.Reachable = false;
        await client.EnqueueAsync(Sale());

        transport.Reachable = true;
        await client.FlushAsync();
        await client.FlushAsync();

        Assert.Single(transport.Posted);
    }

    [Fact]
    public async Task TheBufferSurvivesARestart()
    {
        var db = TempDb();
        var (client, transport) = Build(db);
        transport.Reachable = false;
        var order = await client.EnqueueAsync(Sale());

        var (reopened, secondTransport) = Build(db);
        await reopened.FlushAsync();

        Assert.Single(secondTransport.Posted);
        Assert.Equal(order.Id, secondTransport.Posted[0].Id);
    }
}
