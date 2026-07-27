using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Services.Discounts;

namespace VvCash.Tests;

/// <summary>Hands CartService a fixed promotion set without touching SQLite.</summary>
public sealed class StubPromotionProvider : IPromotionProvider
{
    public StubPromotionProvider(params Promotion[] promotions) => Promotions = promotions;

    public IReadOnlyList<Promotion> Promotions { get; set; }

    public MoneyPolicy MoneyPolicy { get; set; } = MoneyPolicy.Default;

    public Task RefreshAsync() => Task.CompletedTask;
}
