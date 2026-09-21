# Sell-by-Amount Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A third mode on the quantity pad where the cashier types money ("50 сум") and the pad derives the weight, floored to the gram, so the customer never pays over the sum they named.

**Architecture:** `QuantityPadViewModel.EnteredInUnit` (bool) becomes `PadMode Mode` (Pieces / Unit / Money). A single private `Amount` property resolves the typed figure into the amount the line will carry — in money mode `floor3(money / price-per-unit)` — and both the live preview and `Commit` read from it so they cannot disagree. `Commit` still ends in the existing `ICartService.SetQuantity` / `SetQuantityInUnit`; `CartService`, `CartItem`, the parked-sale snapshot and the server document do not change. The XAML `ToggleSwitch` becomes three `RadioButton.Segmented` bound to per-mode bools, the way the discount modal's `IsDiscountPercentMode` / `IsDiscountAmountMode` pair works.

**Tech Stack:** C# / .NET, Avalonia (reflective XAML bindings — a wrong path builds clean and fails silently), CommunityToolkit.Mvvm `[ObservableProperty]`, xUnit.

**Spec:** [docs/superpowers/specs/2026-09-21-sell-by-amount-design.md](../specs/2026-09-21-sell-by-amount-design.md)

**Branch:** `feat/sell-by-amount` (already created; spec is its first commit).

---

## Files

| File | Responsibility | Change |
|---|---|---|
| `src/VvCash/ViewModels/QuantityPadViewModel.cs` | Pad state, live preview, commit into the cart | `PadMode` enum, mode bools, money → amount derivation, `IsRoundedDown`, `PriceUnitLabel` |
| `src/VvCash/Views/PosView.axaml` (Quantity Pad Modal, ~lines 817–915) | Pad UI | Segmented mode selector replaces `ToggleSwitch`; header binds `PriceUnitLabel`; round-down caption |
| `tests/VvCash.Tests/QuantityPadTest.cs` | Pad behaviour | Existing tests move to `Mode`; new money-mode tests |

No other file changes.

## Conventions for every task

- **Tests:** `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"` from repo root, in the PowerShell tool (no `pwsh` on this box; the script builds to `build/verify-tests` so a running app cannot lock the output). The script stops at the first red test with no summary — read the failing test name from the output, do not count.
- **Build check of the app:** `dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify`. Success line is Russian: `Ошибок: 0`. Never kill a running app to free `bin/`.
- **Commits:** normal prose, Conventional Commits, end with `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Stage only the files named in the task — `main` sometimes carries unrelated WIP.
- **XAML:** every new binding path must be checked in the running app (Task 5), not only by the build.

---

### Task 1: `EnteredInUnit` → `PadMode Mode` (no behaviour change)

Replaces the bool with the enum and per-mode bools, moves the existing tests and the XAML toggle onto it. Money mode is not offered yet — this task only makes room for it.

**Files:**
- Modify: `src/VvCash/ViewModels/QuantityPadViewModel.cs`
- Modify: `src/VvCash/Views/PosView.axaml:850-856` (the `<!-- Unit toggle -->` block)
- Test: `tests/VvCash.Tests/QuantityPadTest.cs`

- [ ] **Step 1: Move the existing tests onto the new API**

In `tests/VvCash.Tests/QuantityPadTest.cs`, change `PriceInSelectedUnit_FollowsTheSelectedUnit`:

```csharp
    [Fact]
    public void PriceInSelectedUnit_FollowsTheSelectedUnit()
    {
        var pad = PadFor(Tile());

        Assert.Equal(416.67m, decimal.Round(pad.PriceInSelectedUnit, 2));
        Assert.Equal("м²", pad.UnitLabel);

        pad.Mode = PadMode.Pieces;

        Assert.Equal(100m, pad.PriceInSelectedUnit);
        Assert.Equal("шт", pad.UnitLabel);
    }
```

Add two tests at the end of the class, before the closing brace:

```csharp
    [Fact]
    public void OpensInTheUnitTheLineWasEnteredIn()
    {
        Assert.Equal(PadMode.Unit, PadFor(Tile(), inUnit: true).Mode);
        Assert.Equal(PadMode.Pieces, PadFor(Tile(), inUnit: false).Mode);

        // EnteredInUnit on a piece-only line is stale data, not a unit to open in.
        var pieceOnly = new Product { Id = "p2", Name = "Товар", Price = 10m };
        Assert.Equal(PadMode.Pieces, PadFor(pieceOnly, inUnit: true).Mode);
    }

    [Fact]
    public void ModeBools_MirrorTheMode_AndOnlyATrueWriteMovesIt()
    {
        var pad = PadFor(Tile(), inUnit: false);

        Assert.True(pad.IsPiecesMode);
        Assert.False(pad.IsUnitMode);

        pad.IsUnitMode = true;
        Assert.Equal(PadMode.Unit, pad.Mode);

        // A RadioButton writes false to the segment it leaves; that must not
        // knock the pad out of the mode it just entered.
        pad.IsPiecesMode = false;
        Assert.Equal(PadMode.Unit, pad.Mode);
    }
```

- [ ] **Step 2: Run the tests to see them fail to compile**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: build error — `'QuantityPadViewModel' does not contain a definition for 'Mode'` / `The type or namespace name 'PadMode' could not be found`.

- [ ] **Step 3: Replace the bool with the enum in the view model**

In `src/VvCash/ViewModels/QuantityPadViewModel.cs`, add the enum above the class (after the `namespace` line):

```csharp
/// <summary>Which figure the cashier is typing into the pad.</summary>
public enum PadMode
{
    /// <summary>A piece count.</summary>
    Pieces,

    /// <summary>An amount in the product's secondary unit — m², kg.</summary>
    Unit,

    /// <summary>Money: "50 somoni of nuggets". The pad derives the weight.</summary>
    Money,
}
```

Replace the constructor and the `_enteredInUnit` field:

```csharp
    public QuantityPadViewModel(CartItem item)
    {
        _item = item;
        // Never Money: that is a one-off gesture, and the line stores a
        // quantity, not the sum it came from.
        _mode = item.EnteredInUnit && item.Product.HasSecondaryUnit ? PadMode.Unit : PadMode.Pieces;
        _input = _mode == PadMode.Unit
            ? item.QuantityInUnit.ToString(CultureInfo.InvariantCulture)
            : item.Quantity.ToString(CultureInfo.InvariantCulture);
    }
```

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPiecesMode), nameof(IsUnitMode), nameof(IsMoneyMode),
        nameof(PriceInSelectedUnit), nameof(UnitLabel), nameof(PreviewQuantity),
        nameof(PreviewQuantityInUnit), nameof(PreviewTotal), nameof(PreviewText),
        nameof(IsRounded), nameof(IsValid))]
    private PadMode _mode;

    // One bool per segment, the way the discount modal's IsDiscountPercentMode /
    // IsDiscountAmountMode pair works. A RadioButton binds IsChecked two-way and
    // writes false to the segment it leaves, which must not move the mode.
    public bool IsPiecesMode { get => Mode == PadMode.Pieces; set { if (value) Mode = PadMode.Pieces; } }
    public bool IsUnitMode { get => Mode == PadMode.Unit; set { if (value) Mode = PadMode.Unit; } }
    public bool IsMoneyMode { get => Mode == PadMode.Money; set { if (value) Mode = PadMode.Money; } }
```

Then add a private helper right after `CanSwitchUnit` and route every former `EnteredInUnit` read through it:

```csharp
    /// <summary>Whether the pad's result is expressed in the secondary unit.</summary>
    private bool DerivesInUnit => Mode == PadMode.Unit;

    public string UnitLabel => DerivesInUnit ? _item.Product.UnitShortName : "шт";
```

Replace each remaining `EnteredInUnit` in the file with `DerivesInUnit`: in `PriceInSelectedUnit`, `IsValid`, `PreviewQuantity`, `PreviewQuantityInUnit`, `IsRounded`, and in `Commit`:

```csharp
    public void Commit(ICartService cart)
    {
        var amount = Parsed;
        if (amount is null || !IsValid) return;

        _item.EnteredInUnit = DerivesInUnit;
        if (DerivesInUnit) cart.SetQuantityInUnit(_item, amount.Value);
        else cart.SetQuantity(_item, amount.Value);
    }
```

After this step `grep -n EnteredInUnit src/VvCash/ViewModels/QuantityPadViewModel.cs` must show only the two `item.EnteredInUnit` / `_item.EnteredInUnit` lines (constructor and `Commit`) — those are the `CartItem` property, which stays.

- [ ] **Step 4: Move the XAML toggle onto the mode bools**

In `src/VvCash/Views/PosView.axaml`, replace the `<!-- Unit toggle -->` comment and the `<ToggleSwitch …/>` that follows it with:

```xml
                    <!-- Mode selector. Hidden entirely for a piece-only product:
                         there is nothing to switch to, and an inert control
                         reads as broken. A StackPanel, not a star Grid, so a
                         hidden segment collapses instead of leaving a gap. -->
                    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Center"
                                IsVisible="{Binding QuantityPad.CanSwitchUnit}">
                        <RadioButton Content="шт" Classes="Segmented" Width="130"
                                     IsChecked="{Binding QuantityPad.IsPiecesMode}"/>
                        <RadioButton Content="{Binding QuantityPad.Item.Product.UnitShortName}" Classes="Segmented" Width="130"
                                     IsChecked="{Binding QuantityPad.IsUnitMode}"
                                     IsVisible="{Binding QuantityPad.CanSwitchUnit}"/>
                    </StackPanel>
```

- [ ] **Step 5: Run the tests**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: all `QuantityPadTest` tests pass (11 tests).

- [ ] **Step 6: Build the app**

Run: `dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify`
Expected: `Ошибок: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/VvCash/ViewModels/QuantityPadViewModel.cs src/VvCash/Views/PosView.axaml tests/VvCash.Tests/QuantityPadTest.cs
git commit -m "refactor(pad): model the entry unit as a mode, not a bool

A third way of entering a line is coming (by money), and a bool has no
room for it. The toggle becomes two segmented buttons bound to per-mode
bools, the way the discount modal already does it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Money mode availability and labels

Adds `CanEnterMoney`, `HasModeSelector`, `PriceUnitLabel`, and teaches `UnitLabel` / `PriceInSelectedUnit` / `DerivesInUnit` about money mode. No arithmetic yet.

**Files:**
- Modify: `src/VvCash/ViewModels/QuantityPadViewModel.cs`
- Test: `tests/VvCash.Tests/QuantityPadTest.cs`

- [ ] **Step 1: Write the failing tests**

Add two product factories next to `Tile` at the top of the test class:

```csharp
    // Nuggets sold by the pack (0.5 kg, 15 each) with kg as the secondary
    // unit, so the price per kg is 30 — the figure the customer is quoted.
    private static Product Nuggets() => new()
    {
        Id = "p3", Name = "Наггетсы", Price = 15m,
        UnitId = "u-kg", UnitCode = "kg", UnitShortName = "кг",
        UnitFactor = 0.5m, IsDivisible = true, SellInSecondaryUnit = true,
    };

    // Weighed goods with no secondary unit: the piece *is* the kilogram.
    private static Product LooseCandy() => new()
    {
        Id = "p4", Name = "Конфеты", Price = 30m, IsDivisible = true,
    };
```

Add these tests at the end of the class:

```csharp
    [Fact]
    public void MoneyMode_IsOfferedOnlyForADivisibleProductWithAPrice()
    {
        Assert.True(PadFor(Nuggets()).CanEnterMoney);
        Assert.True(PadFor(LooseCandy(), inUnit: false).CanEnterMoney);

        // 50 / 3 = 16.67 pieces does not exist.
        Assert.False(PadFor(Tile(divisible: false)).CanEnterMoney);

        // Nothing to divide by.
        var free = LooseCandy();
        free.Price = 0m;
        Assert.False(PadFor(free, inUnit: false).CanEnterMoney);
    }

    [Fact]
    public void ModeSelector_IsShownWhenAnySegmentBesidesPiecesExists()
    {
        Assert.True(PadFor(Tile()).HasModeSelector);                      // unit only
        Assert.True(PadFor(LooseCandy(), inUnit: false).HasModeSelector); // money only

        var pieceOnly = new Product { Id = "p2", Name = "Товар", Price = 10m };
        Assert.False(PadFor(pieceOnly, inUnit: false).HasModeSelector);
    }

    [Fact]
    public void MoneyMode_LabelsTheInputAsMoney_AndTheHeaderInTheDerivedUnit()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        Assert.Equal("сум", pad.UnitLabel);
        Assert.Equal("кг", pad.PriceUnitLabel);
        Assert.Equal(30m, pad.PriceInSelectedUnit);

        var candy = PadFor(LooseCandy(), inUnit: false);
        candy.Mode = PadMode.Money;

        Assert.Equal("сум", candy.UnitLabel);
        Assert.Equal("шт", candy.PriceUnitLabel);
        Assert.Equal(30m, candy.PriceInSelectedUnit);
    }

    [Fact]
    public void PriceUnitLabel_MatchesUnitLabel_OutsideMoneyMode()
    {
        var pad = PadFor(Tile());
        Assert.Equal(pad.UnitLabel, pad.PriceUnitLabel);

        pad.Mode = PadMode.Pieces;
        Assert.Equal(pad.UnitLabel, pad.PriceUnitLabel);
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: build error — `'QuantityPadViewModel' does not contain a definition for 'CanEnterMoney'` (and `HasModeSelector`, `PriceUnitLabel`).

- [ ] **Step 3: Implement**

In `QuantityPadViewModel.cs`, add `PriceUnitLabel` to the `Mode` notify list:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPiecesMode), nameof(IsUnitMode), nameof(IsMoneyMode),
        nameof(PriceInSelectedUnit), nameof(UnitLabel), nameof(PriceUnitLabel),
        nameof(PreviewQuantity), nameof(PreviewQuantityInUnit), nameof(PreviewTotal),
        nameof(PreviewText), nameof(IsRounded), nameof(IsValid))]
    private PadMode _mode;
```

Replace `CanSwitchUnit`, `DerivesInUnit` and `UnitLabel` with:

```csharp
    /// <summary>Whether the piece/unit segment is offered at all. A piece-only
    /// product has nothing to switch to.</summary>
    public bool CanSwitchUnit => _item.Product.HasSecondaryUnit;

    /// <summary>Whether "sell for N" is offered. Only a divisible product can
    /// carry a derived fractional amount — 50 / 3 = 16.67 pieces does not
    /// exist — and a free one has nothing to divide by.</summary>
    public bool CanEnterMoney => _item.Product.IsDivisible && _item.UnitPrice > 0m;

    /// <summary>Whether any segment besides "шт" exists. With none, the whole
    /// selector is hidden: an inert control reads as broken.</summary>
    public bool HasModeSelector => CanSwitchUnit || CanEnterMoney;

    /// <summary>Whether the pad's result is expressed in the secondary unit.
    /// In money mode a product that has one derives in it regardless of
    /// SellInSecondaryUnit: the customer asking for "50 somoni of nuggets"
    /// thinks in kilograms, not packs.</summary>
    private bool DerivesInUnit => Mode switch
    {
        PadMode.Unit => true,
        PadMode.Money => _item.Product.HasSecondaryUnit,
        _ => false,
    };

    /// <summary>Label next to the typed figure: "3 шт", "12.5 м²", "50 сум".</summary>
    public string UnitLabel => Mode switch
    {
        PadMode.Unit => _item.Product.UnitShortName,
        PadMode.Money => "сум",
        _ => "шт",
    };

    /// <summary>Unit the entered figure resolves to, for the "price / unit"
    /// header. Differs from <see cref="UnitLabel"/> only in money mode, where
    /// the header must read "30.00 / кг" and not "30.00 / сум".</summary>
    public string PriceUnitLabel => DerivesInUnit ? _item.Product.UnitShortName : "шт";
```

`PriceInSelectedUnit` already reads `DerivesInUnit`, so in money mode on a product with a secondary unit it now yields the per-unit price (15 / 0.5 = 30) with no further change. Update its doc comment's first line to: `/// <summary>Price expressed in the unit the entry resolves to, so the ticket`.

- [ ] **Step 4: Run the tests**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: all pass (15 tests).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/QuantityPadViewModel.cs tests/VvCash.Tests/QuantityPadTest.cs
git commit -m "feat(pad): offer a money mode on divisible products

Only availability and labels for now: the segment exists for a divisible
product with a price, the input reads in somoni, and the header keeps
showing the price per kilogram the cashier is about to divide by.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Derive the amount from money, floored to the gram

The core: a single `Amount` property both the preview and `Commit` read from. In money mode it is `floor3(money / PriceInSelectedUnit)`; otherwise the typed figure.

**Files:**
- Modify: `src/VvCash/ViewModels/QuantityPadViewModel.cs`
- Test: `tests/VvCash.Tests/QuantityPadTest.cs`

- [ ] **Step 1: Write the failing tests**

Append to the test class:

```csharp
    [Fact]
    public void MoneyMode_DerivesTheWeight_FlooredToTheGram()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        pad.Input = "50";

        // 50 / 30 = 1.6666…; the customer named 50, so it is cut, not rounded.
        Assert.Equal(1.666m, pad.PreviewQuantityInUnit);
        Assert.Equal(3.332m, pad.PreviewQuantity);
        Assert.Equal(49.98m, pad.PreviewTotal);
        Assert.True(pad.IsRoundedDown);
        Assert.False(pad.IsRounded);
        Assert.True(pad.IsValid);
    }

    [Fact]
    public void MoneyMode_DerivesPieces_WhenThereIsNoSecondaryUnit()
    {
        var pad = PadFor(LooseCandy(), inUnit: false);
        pad.Mode = PadMode.Money;

        pad.Input = "50";

        Assert.Equal(1.666m, pad.PreviewQuantity);
        Assert.Equal(0m, pad.PreviewQuantityInUnit);
        Assert.Equal(49.98m, pad.PreviewTotal);
        Assert.True(pad.IsRoundedDown);
        Assert.True(pad.IsValid);
    }

    [Fact]
    public void MoneyMode_DoesNotFlagRoundDown_OnAnExactDivision()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        pad.Input = "60";

        Assert.Equal(2m, pad.PreviewQuantityInUnit);
        Assert.Equal(60m, pad.PreviewTotal);
        Assert.False(pad.IsRoundedDown);
    }

    [Fact]
    public void MoneyMode_RejectsASumBelowOneGram()
    {
        var pad = PadFor(Nuggets());
        pad.Mode = PadMode.Money;

        pad.Input = "0.01";

        Assert.False(pad.IsValid);
        Assert.Equal(0m, pad.PreviewQuantity);
        Assert.False(pad.IsRoundedDown);
    }

    [Fact]
    public void MoneyMode_DividesByTheQuotedPrice_NotTheCatalogueOne()
    {
        var item = new CartItem { Product = LooseCandy(), Quantity = 1m, QuotedUnitPrice = 25m };
        var pad = new QuantityPadViewModel(item);
        pad.Mode = PadMode.Money;

        pad.Input = "50";

        Assert.Equal(2m, pad.PreviewQuantity);
        Assert.Equal(50m, pad.PreviewTotal);
    }

    [Fact]
    public void IsRoundedDown_IsFalse_OutsideMoneyMode()
    {
        var pad = PadFor(Nuggets());
        pad.Input = "1.6666";

        Assert.False(pad.IsRoundedDown);
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: build error — `'QuantityPadViewModel' does not contain a definition for 'IsRoundedDown'`.

- [ ] **Step 3: Implement**

In `QuantityPadViewModel.cs`, add the constant at the top of the class, above `_item`:

```csharp
    /// <summary>Where a money-derived amount is cut: scales weigh in grams,
    /// and the cashier weighs out exactly the figure on screen.</summary>
    private const decimal WeightStep = 0.001m;
```

Add `IsRoundedDown` to both notify lists (`_input` and `_mode`):

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewQuantity), nameof(PreviewQuantityInUnit),
        nameof(PreviewTotal), nameof(PreviewText), nameof(IsRounded), nameof(IsRoundedDown), nameof(IsValid))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPiecesMode), nameof(IsUnitMode), nameof(IsMoneyMode),
        nameof(PriceInSelectedUnit), nameof(UnitLabel), nameof(PriceUnitLabel),
        nameof(PreviewQuantity), nameof(PreviewQuantityInUnit), nameof(PreviewTotal),
        nameof(PreviewText), nameof(IsRounded), nameof(IsRoundedDown), nameof(IsValid))]
    private PadMode _mode;
```

Directly after the existing `Parsed` property, add:

```csharp
    /// <summary>The amount the pad resolves to, in <see cref="PriceUnitLabel"/>'s
    /// unit: the typed figure, or in money mode the sum divided by the price
    /// and floored to the gram. Null when there is nothing valid to resolve.
    /// Shared by the preview and <see cref="Commit"/> so the two cannot
    /// disagree.</summary>
    private decimal? Amount
    {
        get
        {
            var typed = Parsed;
            if (typed is null) return null;
            if (Mode != PadMode.Money) return typed;
            if (!CanEnterMoney) return null;

            // Floor, not round: the customer named the sum, so the line must
            // not come out above it. 50 / 30 → 1.666 kg → 49.98.
            var amount = FloorToWeight(typed.Value / PriceInSelectedUnit);
            return amount > 0m ? amount : null;
        }
    }

    private static decimal FloorToWeight(decimal value)
        => decimal.Truncate(value / WeightStep) * WeightStep;
```

Then switch `IsValid`, `PreviewQuantity`, `PreviewQuantityInUnit` and `IsRounded` from `Parsed` to `Amount` — each currently opens with `var amount = Parsed;`; change that line to `var amount = Amount;` in all four. In `IsRounded` also change the mode guard so money mode never flags it:

```csharp
    public bool IsRounded
    {
        get
        {
            var amount = Amount;
            if (amount is null || Mode != PadMode.Unit) return false;
            return PreviewQuantityInUnit != amount.Value;
        }
    }
```

Add `IsRoundedDown` right after `IsRounded`:

```csharp
    /// <summary>Whether the derived weight was cut below the sum the customer
    /// named — the money-mode counterpart of <see cref="IsRounded"/>. 50 / 30
    /// = 1.666 kg bills 49.98, and the cashier should see that before the
    /// receipt does.</summary>
    public bool IsRoundedDown
    {
        get
        {
            if (Mode != PadMode.Money || Amount is null) return false;
            return PreviewTotal < Parsed!.Value;
        }
    }
```

`Commit` keeps reading `Parsed` for now — Task 4 moves it.

- [ ] **Step 4: Run the tests**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: all pass (21 tests).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/QuantityPadViewModel.cs tests/VvCash.Tests/QuantityPadTest.cs
git commit -m "feat(pad): derive the weight from a typed sum, floored to the gram

50 somoni at 30 per kilogram is 1.666 kg and bills 49.98: the customer
named the sum, so the line is cut below it rather than rounded over it.
The preview flags the cut the same way it flags a round-up on tiles.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Commit the derived amount into the cart

**Files:**
- Modify: `src/VvCash/ViewModels/QuantityPadViewModel.cs` (`Commit`)
- Test: `tests/VvCash.Tests/QuantityPadTest.cs`

- [ ] **Step 1: Write the failing tests**

Add `using VvCash.Services;` to the top of `QuantityPadTest.cs` (after `using VvCash.Models;`). Append to the class:

```csharp
    [Fact]
    public void Commit_InMoneyMode_SetsTheLineInTheSecondaryUnit()
    {
        var cart = new CartService(new StubPromotionProvider());
        cart.AddProduct(Nuggets());
        var item = cart.Items[0];
        var pad = new QuantityPadViewModel(item);
        pad.Mode = PadMode.Money;
        pad.Input = "50";

        pad.Commit(cart);

        Assert.Equal(1.666m, item.QuantityInUnit);
        Assert.Equal(3.332m, item.Quantity);
        Assert.True(item.EnteredInUnit);
        Assert.Equal(49.98m, item.LineTotal);
    }

    [Fact]
    public void Commit_InMoneyMode_SetsPieces_WhenThereIsNoSecondaryUnit()
    {
        var cart = new CartService(new StubPromotionProvider());
        cart.AddProduct(LooseCandy());
        var item = cart.Items[0];
        var pad = new QuantityPadViewModel(item);
        pad.Mode = PadMode.Money;
        pad.Input = "50";

        pad.Commit(cart);

        Assert.Equal(1.666m, item.Quantity);
        Assert.False(item.EnteredInUnit);
        Assert.Equal(49.98m, item.LineTotal);
    }

    [Fact]
    public void Commit_InMoneyMode_LeavesTheLineAlone_WhenTheSumIsBelowOneGram()
    {
        var cart = new CartService(new StubPromotionProvider());
        cart.AddProduct(Nuggets());
        var item = cart.Items[0];
        var pad = new QuantityPadViewModel(item);
        pad.Mode = PadMode.Money;
        pad.Input = "0.01";

        pad.Commit(cart);

        Assert.Equal(1m, item.Quantity);
        Assert.Single(cart.Items);
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: `Commit_InMoneyMode_SetsTheLineInTheSecondaryUnit` fails — `Assert.Equal() Failure: Expected: 1.666, Actual: 50` (Commit still writes the typed sum as the amount).

- [ ] **Step 3: Implement**

Replace `Commit`:

```csharp
    /// <summary>Writes the pad's result back through the cart, which is what
    /// recomputes totals and re-prices the cart. In money mode the line is
    /// set from the derived amount; the sum itself is not kept — the line
    /// stores a quantity, and reopening the pad shows that quantity.</summary>
    public void Commit(ICartService cart)
    {
        var amount = Amount;
        if (amount is null || !IsValid) return;

        _item.EnteredInUnit = DerivesInUnit;
        if (DerivesInUnit) cart.SetQuantityInUnit(_item, amount.Value);
        else cart.SetQuantity(_item, amount.Value);
    }
```

- [ ] **Step 4: Run the tests**

Run: `& ./run-tests.ps1 --filter "FullyQualifiedName~QuantityPadTest"`
Expected: all pass (24 tests).

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/ViewModels/QuantityPadViewModel.cs tests/VvCash.Tests/QuantityPadTest.cs
git commit -m "feat(pad): commit a money entry as the derived weight

The line ends up as an ordinary 1.666 kg through the existing cart
setters; the cart, the parked-sale snapshot and the server never learn
it came from a sum.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: The third segment and the round-down caption in XAML

**Files:**
- Modify: `src/VvCash/Views/PosView.axaml` (Quantity Pad Modal)

- [ ] **Step 1: Bind the header to the derived unit**

In the pad header, the `MultiBinding StringFormat="{}{0:F2} / {1}"` currently binds `QuantityPad.PriceInSelectedUnit` and `QuantityPad.UnitLabel`. Change the second binding:

```xml
                                    <MultiBinding StringFormat="{}{0:F2} / {1}">
                                        <Binding Path="QuantityPad.PriceInSelectedUnit"/>
                                        <Binding Path="QuantityPad.PriceUnitLabel"/>
                                    </MultiBinding>
```

The input box further down keeps `{Binding QuantityPad.UnitLabel}` — that is the "50 сум" label.

- [ ] **Step 2: Add the money segment**

Replace the selector `StackPanel` from Task 1 with:

```xml
                    <!-- Mode selector. Hidden entirely for a piece-only,
                         indivisible product: there is nothing to switch to,
                         and an inert control reads as broken. A StackPanel,
                         not a star Grid, so a hidden segment collapses
                         instead of leaving a gap. -->
                    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Center"
                                IsVisible="{Binding QuantityPad.HasModeSelector}">
                        <RadioButton Content="шт" Classes="Segmented" Width="130"
                                     IsChecked="{Binding QuantityPad.IsPiecesMode}"/>
                        <RadioButton Content="{Binding QuantityPad.Item.Product.UnitShortName}" Classes="Segmented" Width="130"
                                     IsChecked="{Binding QuantityPad.IsUnitMode}"
                                     IsVisible="{Binding QuantityPad.CanSwitchUnit}"/>
                        <RadioButton Content="сум" Classes="Segmented" Width="130"
                                     IsChecked="{Binding QuantityPad.IsMoneyMode}"
                                     IsVisible="{Binding QuantityPad.CanEnterMoney}"/>
                    </StackPanel>
```

- [ ] **Step 3: Add the round-down caption**

Directly after the existing `IsRounded` `TextBlock` ("Округлено вверх до целой штуки"), inside the same `StackPanel`:

```xml
                        <TextBlock IsVisible="{Binding QuantityPad.IsRoundedDown}"
                                   Text="Округлено вниз до грамма"
                                   FontSize="12" HorizontalAlignment="Center"
                                   Foreground="{StaticResource Red600Brush}"/>
```

- [ ] **Step 4: Build the app**

Run: `dotnet build src/VvCash/VvCash.csproj -c Debug -o build/verify`
Expected: `Ошибок: 0`.

- [ ] **Step 5: Check every new binding in the running app**

Bindings are reflective: a typo builds clean and the control just stays blank. Launch the app (use the `run` skill, or ask the user to run it) with a catalogue that has a divisible product with a kg unit and a divisible product without a unit, then walk this list and tick each line:

- Tap the quantity on a **kg product** line: three segments `шт | кг | сум`; header reads `<price> / кг` in every segment; tapping `сум` relabels the input box to `сум` and keeps the header at `/ кг`.
- Type `50` in `сум`: preview reads `→ 3.332 шт = 1.666 кг · 49.98` (for 30/kg) and the red caption `Округлено вниз до грамма` is shown; type `60`: caption gone.
- Type `0.01`: `Применить` is disabled.
- Apply `50`: the cart line reads `1.666 кг`, total 49.98. Reopen the pad on that line: it opens in `кг` with `1.666`, not in `сум`.
- **Divisible product without a unit**: segments `шт | сум` only, no gap between them; header `/ шт`; `50` → `→ 1.666 шт · 49.98`.
- **Indivisible tile**: segments `шт | м²` only; the round-up caption still works as before.
- **Plain piece product**: no selector at all.

Fix any blank control or stale label before committing — the fix is a binding path in this file.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Views/PosView.axaml
git commit -m "feat(pad): sell a weighed product for a named sum

Third segment on the quantity pad: the cashier types the somoni the
customer asked for, the pad shows the weight and what it bills, and
the header keeps the per-kilogram price the sum is divided by.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Full test run and spec status

**Files:**
- Modify: `docs/superpowers/specs/2026-09-21-sell-by-amount-design.md` (status line)

- [ ] **Step 1: Run the whole suite**

Run: `& ./run-tests.ps1`
Expected: green. The suite has a known Avalonia `Dispatcher` race that fails one random test now and then — if a red test's stack trace is in `Avalonia.Threading`, rerun once; if it is in `QuantityPad*` or `CartService*`, it is this branch's.

- [ ] **Step 2: Mark the spec implemented**

Change the spec's `**Статус:**` line to `**Статус:** реализовано в ветке `feat/sell-by-amount``.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs/2026-09-21-sell-by-amount-design.md
git commit -m "docs(pad): mark the sell-by-amount spec implemented

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

Then hand over to `superpowers:finishing-a-development-branch`.
