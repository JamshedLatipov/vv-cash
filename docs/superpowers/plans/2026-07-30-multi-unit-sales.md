# Multi-Unit Sales Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a cashier ring up goods in a secondary unit of measure (m², running metre, kg) instead of converting to pieces in their head.

**Architecture:** The cart stays denominated in pieces — money, promotions and quotes are untouched. A pure `UnitConverter` mirrors the server's `units.ConvertToBase` so both sides agree on the piece count; the unit fields ride along with each `Product` from sync, through SQLite, into the cart line, and back out as the document's `unit_id`/`unit_factor`/`quantity_in_unit` trio. UI comes last: a quantity pad that does not exist today.

**Tech Stack:** .NET 8, Avalonia UI, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit.

**Spec:** [`docs/superpowers/specs/2026-07-30-multi-unit-sales-design.md`](../specs/2026-07-30-multi-unit-sales-design.md)

---

## Before you start

Run the whole suite once so you know it was green before you touched it:

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1"
```

Never run `dotnet build` against the default output directory — a running app instance locks it. `run-tests.ps1` already builds to `build/verify-tests`. For a bare compile check use:

```bash
cd /c/work/vv-cash && dotnet build src/VvCash/VvCash.csproj -o build/verify
```

The working tree carries unrelated uncommitted work in `src/VvCash/Models/CartItem.cs`, `src/VvCash/Services/CartService.cs` and `tests/VvCash.Tests/CartServiceQuoteTest.cs`. Stage files by name in every commit below. Never use `git add -A` or `git commit -a`.

## File structure

| File | Responsibility |
| --- | --- |
| `src/VvCash/Services/UnitConverter.cs` (new) | Pure piece ↔ unit arithmetic, mirror of the server's `units` package |
| `src/VvCash/Models/Product.cs` | Carries the secondary unit synced from the server |
| `src/VvCash/Services/Data/SyncService.cs` | Parses the unit keys off the sync payload |
| `src/VvCash/Services/Data/OfflineStorageService.cs` | Persists the unit columns |
| `src/VvCash/Models/CartItem.cs` | Holds the entry mode and the unit snapshot for one line |
| `src/VvCash/Services/CartService.cs`, `ICartService.cs` | Entry point for setting a quantity in units |
| `src/VvCash/Models/Api/DocumentRequest.cs` | The unit trio on the wire |
| `src/VvCash/ViewModels/PosViewModel.cs` | Fills the trio; owns the quantity-pad modal state |
| `src/VvCash/Views/PosView.axaml` | Cart line display + quantity-pad modal |
| `src/VvCash/Services/Hardware/EscPosPrinterService.cs` | Receipt line |
| `src/VvCash/Models/ParkedSaleSnapshot.cs` | Entry mode survives park/unpark |

Tasks 1–9 are backend-of-the-register work and each ends green. Task 10 is the UI and depends on all of them.

---

### Task 1: UnitConverter

The only piece of arithmetic that must agree with the server bit for bit. Build it first and alone.

**Files:**
- Create: `src/VvCash/Services/UnitConverter.cs`
- Test: `tests/VvCash.Tests/UnitConverterTest.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/UnitConverterTest.cs`:

```csharp
using System;
using System.Globalization;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

// The server re-derives every line it is sent: it rejects the document unless
// |quantity_in_unit - quantity * factor| stays inside ToleranceFor(factor).
// A register that rounds even slightly differently gets its own honest,
// already-printed receipts refused, so these cases are pinned against the
// server's units.ConvertToBase rather than against intuition.
public class UnitConverterTest
{
    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    [Theory]
    // Divisible: pieces are the derived figure, the entered amount is kept as-is.
    [InlineData("12.5", "0.24", true, "52.083333", "12.5")]
    [InlineData("3.75", "2.5", true, "1.5", "3.75")]
    // Indivisible: pieces round UP and the unit amount is recomputed from them,
    // because the customer is charged for whole tiles.
    [InlineData("12.5", "0.24", false, "53", "12.72")]
    // Exact multiple must NOT round up - that would bill one tile too many.
    [InlineData("12.0", "0.24", false, "50", "12.0")]
    public void ToBase_MatchesServerConversion(
        string amount, string factor, bool isDivisible, string wantQty, string wantQtyInUnit)
    {
        var (qty, qtyInUnit) = UnitConverter.ToBase(D(amount), D(factor), isDivisible);

        Assert.Equal(D(wantQty), qty);
        Assert.Equal(D(wantQtyInUnit), qtyInUnit);
    }

    [Theory]
    [InlineData("12.5", "0.24", true)]
    [InlineData("3.75", "2.5", true)]
    [InlineData("12.5", "0.24", false)]
    [InlineData("12.0", "0.24", false)]
    [InlineData("0.0000025", "1", true)]
    public void ToBase_SatisfiesServerSnapshotTolerance(string amount, string factor, bool isDivisible)
    {
        var f = D(factor);
        var (qty, qtyInUnit) = UnitConverter.ToBase(D(amount), f, isDivisible);

        // units.ToleranceFor: max(0.001, factor * 1e-6).
        var tolerance = Math.Max(0.001m, f * 0.000001m);
        Assert.True(Math.Abs(qtyInUnit - qty * f) <= tolerance,
            $"snapshot drift {Math.Abs(qtyInUnit - qty * f)} exceeds tolerance {tolerance}");
    }

    [Fact]
    public void ToBase_RoundsHalfAwayFromZero_NotBankers()
    {
        // 0.0000025 sits exactly on the 6th-decimal midpoint with an even digit
        // before it, which is the only place the two rounding modes disagree:
        // the server's DivRound gives 0.000003, .NET's default MidpointRounding
        // .ToEven would give 0.000002.
        var (qty, _) = UnitConverter.ToBase(D("0.0000025"), 1m, isDivisible: true);

        Assert.Equal(D("0.000003"), qty);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ToBase_RejectsNonPositiveFactor(string factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnitConverter.ToBase(1m, D(factor), isDivisible: true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ToBase_RejectsNonPositiveAmount(string amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnitConverter.ToBase(D(amount), 0.24m, isDivisible: true));
    }

    [Fact]
    public void ToUnit_IsTheReverseView()
    {
        Assert.Equal(12.72m, UnitConverter.ToUnit(53m, 0.24m));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~UnitConverterTest"
```

Expected: build error, `The name 'UnitConverter' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/VvCash/Services/UnitConverter.cs`:

```csharp
using System;

namespace VvCash.Services;

/// <summary>Converts between a product's base unit — always a piece — and its
/// secondary unit (m², running metre, kilogram…).
///
/// A deliberate mirror of the server's <c>units.ConvertToBase</c>. The server
/// re-derives every line it receives and refuses the document when
/// <c>|quantity_in_unit − quantity × factor|</c> leaves its tolerance, so a
/// register that rounds differently gets its own already-printed receipts
/// rejected. Any change here has to be made on both sides at once.</summary>
public static class UnitConverter
{
    /// <summary>Where the piece count is cut for divisible goods. 12.5 / 0.24
    /// does not terminate, so the cut is explicit and identical on every
    /// client. Matches the server's <c>divisibleScale</c>.</summary>
    public const int DivisibleScale = 6;

    /// <summary>Turns an amount in the secondary unit into pieces.
    ///
    /// <paramref name="factor"/> is how many secondary units fit into one
    /// piece: 0.24 m² per tile.
    ///
    /// For an indivisible product the piece count rounds up and the returned
    /// unit amount is recomputed from it — the customer pays for whole pieces.
    /// Returning the requested amount instead would break the server's
    /// quantity × factor ≈ quantity_in_unit invariant.</summary>
    public static (decimal Quantity, decimal QuantityInUnit) ToBase(
        decimal amount, decimal factor, bool isDivisible)
    {
        if (factor <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(factor), factor, "unit factor must be greater than zero");
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "amount must be greater than zero");

        if (isDivisible)
        {
            // AwayFromZero, not .NET's default ToEven: the server's DivRound
            // rounds half away from zero, and banker's rounding would put the
            // two sides on different piece counts for an exact midpoint.
            var pieces = Math.Round(amount / factor, DivisibleScale, MidpointRounding.AwayFromZero);
            return (pieces, amount);
        }

        // Exact remainder rather than Math.Ceiling over the quotient. decimal
        // division rounds at the 28th significant digit, so a small factor with
        // a large amount can produce a quotient that has already rounded up,
        // and Ceiling would then add a piece nobody asked for. decimal
        // multiplication is exact, so comparing the product back against the
        // amount is safe at any factor. The server takes the same precaution
        // with QuoRem instead of Div.
        var whole = decimal.Truncate(amount / factor);
        if (whole * factor < amount) whole += 1m;
        return (whole, whole * factor);
    }

    /// <summary>The reverse view: how many secondary units a piece count amounts
    /// to. Used for display, and to recompute a line whose piece count was
    /// changed by the +/− stepper.</summary>
    public static decimal ToUnit(decimal quantity, decimal factor) => quantity * factor;
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~UnitConverterTest"
```

Expected: `Passed!` with 15 tests.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/UnitConverter.cs tests/VvCash.Tests/UnitConverterTest.cs
git commit -m "feat(units): mirror the server's piece/unit conversion on the register"
```

---

### Task 2: Unit fields on Product

**Files:**
- Modify: `src/VvCash/Models/Product.cs`
- Test: `tests/VvCash.Tests/ProductUnitTest.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/ProductUnitTest.cs`:

```csharp
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class ProductUnitTest
{
    [Fact]
    public void HasSecondaryUnit_IsFalse_ForAPieceOnlyProduct()
    {
        Assert.False(new Product { Id = "p1" }.HasSecondaryUnit);
    }

    [Fact]
    public void HasSecondaryUnit_IsTrue_WhenIdAndFactorAreBothSet()
    {
        var p = new Product { Id = "p1", UnitId = "u1", UnitFactor = 0.24m };

        Assert.True(p.HasSecondaryUnit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HasSecondaryUnit_IsFalse_WhenTheFactorIsNotPositive(int factor)
    {
        // A filled unit id with a zero or negative factor is a broken product
        // card. Reading it as piece-only keeps the register selling instead of
        // dividing by zero at the till.
        var p = new Product { Id = "p1", UnitId = "u1", UnitFactor = factor };

        Assert.False(p.HasSecondaryUnit);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~ProductUnitTest"
```

Expected: build error, `'Product' does not contain a definition for 'UnitId'`.

- [ ] **Step 3: Write the implementation**

In `src/VvCash/Models/Product.cs`, insert after the `Barcode` property (currently line 23) and before the `ImageBitmap` field:

```csharp
    /// <summary>Secondary unit of measure — empty when the product is sold by
    /// the piece only, which is the overwhelmingly common case. The register
    /// converts while offline, so the whole unit travels with the product
    /// during sync rather than being asked for at sale time.
    ///
    /// The id is not decoration: the server matches the document line's
    /// unit_id against the product's own unit and rejects the line otherwise,
    /// so code and short name — which are display strings — cannot stand in
    /// for it.</summary>
    public string UnitId { get; set; } = string.Empty;
    public string UnitCode { get; set; } = string.Empty;
    public string UnitShortName { get; set; } = string.Empty;

    /// <summary>How many secondary units fit into one piece: 0.24 m² per tile.
    /// Decimal rather than double because it feeds the snapshot the server
    /// re-checks against its own tolerance, and binary float drift compounds
    /// across a hundred-piece line.</summary>
    public decimal UnitFactor { get; set; }

    /// <summary>Whether a fractional piece may be sold. False for tiles: half a
    /// tile does not exist, so an order rounds up to the next whole one.</summary>
    public bool IsDivisible { get; set; }

    /// <summary>Which unit the quantity pad opens in, decided once on the
    /// product card. Tiles are ordered in m² and rolls by the piece, and the
    /// cashier should not have to know which is which.</summary>
    public bool SellInSecondaryUnit { get; set; }

    /// <summary>Whether this product can be sold in a secondary unit at all.
    /// A non-positive factor against a filled unit id is a broken card and
    /// reads as piece-only rather than taking the sale down.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSecondaryUnit => !string.IsNullOrEmpty(UnitId) && UnitFactor > 0m;
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~ProductUnitTest"
```

Expected: `Passed!` with 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Models/Product.cs tests/VvCash.Tests/ProductUnitTest.cs
git commit -m "feat(units): carry the secondary unit on Product"
```

---

### Task 3: Parse the unit keys during sync

**Files:**
- Modify: `src/VvCash/Services/Data/SyncService.cs` (product parsing block, around lines 150–223)
- Test: `tests/VvCash.Tests/SyncServiceTest.cs`

- [ ] **Step 1: Write the failing test**

Append these two tests to `tests/VvCash.Tests/SyncServiceTest.cs`, inside the `SyncServiceTest` class (copy the shape of the existing `SyncProductsAsync_ParsesTagIds`):

```csharp
    [Fact]
    public async Task SyncProductsAsync_ParsesUnitFields()
    {
        // The register converts m2 to pieces with no server to ask, so the whole
        // unit has to arrive during sync. unit_id especially: the document
        // validator matches it against the product's own unit.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1],"status":0}""");
            if (url.Contains("product/update/1/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"p1","name":"Плитка","sell_price":100,"unit_id":"u-1","unit_code":"m2","unit_short_name":"м²","unit_factor":0.24,"is_divisible":false,"sell_in_secondary_unit":true}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var product = Assert.Single(storage.SavedProducts);
        Assert.Equal("u-1", product.UnitId);
        Assert.Equal("m2", product.UnitCode);
        Assert.Equal("м²", product.UnitShortName);
        Assert.Equal(0.24m, product.UnitFactor);
        Assert.False(product.IsDivisible);
        Assert.True(product.SellInSecondaryUnit);
        Assert.True(product.HasSecondaryUnit);
    }

    [Fact]
    public async Task SyncProductsAsync_TreatsAMissingUnitAsPieceOnly()
    {
        // Most products have no secondary unit, and a backend older than the
        // units module sends none of these keys at all. Neither may break sync.
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("product/versions/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[1],"status":0}""");
            if (url.Contains("product/update/1/"))
                return (HttpStatusCode.OK, """{"message":"success","body":[{"id":"p1","name":"Товар","sell_price":10}],"status":0}""");
            return (HttpStatusCode.OK, """{"message":"success","body":null,"status":0}""");
        });
        var storage = new FakeStorage();

        await Build(handler, storage).SyncProductsAsync();

        var product = Assert.Single(storage.SavedProducts);
        Assert.Equal(string.Empty, product.UnitId);
        Assert.Equal(0m, product.UnitFactor);
        Assert.False(product.HasSecondaryUnit);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~SyncServiceTest"
```

Expected: `SyncProductsAsync_ParsesUnitFields` FAILS — `Assert.Equal() Failure: Expected: u-1, Actual: (empty)`. `SyncProductsAsync_TreatsAMissingUnitAsPieceOnly` already passes; that is fine, it is a regression guard.

- [ ] **Step 3: Write the implementation**

In `src/VvCash/Services/Data/SyncService.cs`, immediately after the `tagIds` parsing loop and before the `Console.WriteLine($"[SyncService] Product ...")` line, insert:

```csharp
                                                        // Secondary unit. Every key is optional: a piece-only product
                                                        // carries none of them, and a backend older than the units
                                                        // module sends none at all. Both read as "sold by the piece".
                                                        var unitId = string.Empty;
                                                        var unitCode = string.Empty;
                                                        var unitShortName = string.Empty;
                                                        var unitFactor = 0m;
                                                        var isDivisible = false;
                                                        var sellInSecondaryUnit = false;

                                                        if (item.TryGetProperty("unit_id", out var unitIdElem) && unitIdElem.ValueKind == JsonValueKind.String)
                                                            unitId = unitIdElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("unit_code", out var unitCodeElem) && unitCodeElem.ValueKind == JsonValueKind.String)
                                                            unitCode = unitCodeElem.GetString() ?? string.Empty;

                                                        if (item.TryGetProperty("unit_short_name", out var unitShortElem) && unitShortElem.ValueKind == JsonValueKind.String)
                                                            unitShortName = unitShortElem.GetString() ?? string.Empty;

                                                        // GetDecimal, not GetDouble: the factor ends up in the snapshot
                                                        // the server re-checks against its tolerance.
                                                        if (item.TryGetProperty("unit_factor", out var unitFactorElem) && unitFactorElem.ValueKind == JsonValueKind.Number)
                                                            unitFactor = unitFactorElem.GetDecimal();

                                                        if (item.TryGetProperty("is_divisible", out var divisibleElem) &&
                                                            (divisibleElem.ValueKind == JsonValueKind.True || divisibleElem.ValueKind == JsonValueKind.False))
                                                            isDivisible = divisibleElem.GetBoolean();

                                                        if (item.TryGetProperty("sell_in_secondary_unit", out var sellInUnitElem) &&
                                                            (sellInUnitElem.ValueKind == JsonValueKind.True || sellInUnitElem.ValueKind == JsonValueKind.False))
                                                            sellInSecondaryUnit = sellInUnitElem.GetBoolean();
```

Then extend the `new Product { ... }` initialiser (currently ending at `TagIds = tagIds`) so it reads:

```csharp
                                                            TagIds = tagIds,
                                                            UnitId = unitId,
                                                            UnitCode = unitCode,
                                                            UnitShortName = unitShortName,
                                                            UnitFactor = unitFactor,
                                                            IsDivisible = isDivisible,
                                                            SellInSecondaryUnit = sellInSecondaryUnit
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~SyncServiceTest"
```

Expected: `Passed!`, all `SyncServiceTest` tests green.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/Data/SyncService.cs tests/VvCash.Tests/SyncServiceTest.cs
git commit -m "feat(units): parse the secondary unit off the sync payload"
```

---

### Task 4: Persist the unit columns

**Files:**
- Modify: `src/VvCash/Services/Data/OfflineStorageService.cs`
- Test: `tests/VvCash.Tests/OfflineStorageServiceTest.cs`

- [ ] **Step 1: Write the failing test**

Append to the `OfflineStorageServiceTest` class in `tests/VvCash.Tests/OfflineStorageServiceTest.cs`:

```csharp
    [Fact]
    public async Task SaveProductsAsync_RoundTripsTheSecondaryUnit()
    {
        await _service.SaveProductsAsync(new[]
        {
            new Product
            {
                Id = "p1", Name = "Плитка", Price = 100m,
                UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
                UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
            },
        });

        var product = Assert.Single(await _service.GetAllProductsAsync());
        Assert.Equal("u-1", product.UnitId);
        Assert.Equal("m2", product.UnitCode);
        Assert.Equal("м²", product.UnitShortName);
        Assert.Equal(0.24m, product.UnitFactor);
        Assert.False(product.IsDivisible);
        Assert.True(product.SellInSecondaryUnit);
    }

    [Fact]
    public async Task SaveProductsAsync_RoundTripsAPieceOnlyProduct()
    {
        await _service.SaveProductsAsync(new[]
        {
            new Product { Id = "p2", Name = "Товар", Price = 10m },
        });

        var product = Assert.Single(await _service.GetAllProductsAsync());
        Assert.Equal(string.Empty, product.UnitId);
        Assert.Equal(0m, product.UnitFactor);
        Assert.False(product.HasSecondaryUnit);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~OfflineStorageServiceTest"
```

Expected: `SaveProductsAsync_RoundTripsTheSecondaryUnit` FAILS — `Assert.Equal() Failure: Expected: u-1, Actual: (empty)`.

- [ ] **Step 3: Write the implementation**

Four edits in `src/VvCash/Services/Data/OfflineStorageService.cs`.

**3a.** In the `CREATE TABLE IF NOT EXISTS Products` block (currently lines 59–70), append the new columns after `Tags TEXT`:

```sql
            CREATE TABLE IF NOT EXISTS Products (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Sku TEXT,
                Category TEXT,
                Price REAL NOT NULL,
                OriginalPrice REAL,
                DiscountPercent REAL,
                ImagePath TEXT,
                Barcode TEXT,
                Tags TEXT,
                UnitId TEXT,
                UnitCode TEXT,
                UnitShortName TEXT,
                UnitFactor REAL,
                IsDivisible INTEGER,
                SellInSecondaryUnit INTEGER
            );
```

**3b.** After the existing `ALTER TABLE Products ADD COLUMN Tags TEXT;` migration block (currently lines 130–136) and before `_isInitialized = true;`, add — matching the surrounding idiom exactly, one statement per try so a register upgrading from any older schema picks up whichever columns it lacks:

```csharp
        // Migration: add the secondary-unit columns to Products if upgrading
        // from an older DB. One ALTER per column, because a register may be
        // upgrading from any point in this sequence.
        foreach (var alter in new[]
        {
            "ALTER TABLE Products ADD COLUMN UnitId TEXT;",
            "ALTER TABLE Products ADD COLUMN UnitCode TEXT;",
            "ALTER TABLE Products ADD COLUMN UnitShortName TEXT;",
            "ALTER TABLE Products ADD COLUMN UnitFactor REAL;",
            "ALTER TABLE Products ADD COLUMN IsDivisible INTEGER;",
            "ALTER TABLE Products ADD COLUMN SellInSecondaryUnit INTEGER;",
        })
        {
            try
            {
                command.CommandText = alter;
                await command.ExecuteNonQueryAsync();
            }
            catch { /* column already exists */ }
        }
```

**3c.** In `SaveProductsAsync`, replace the `command.CommandText` assignment and the parameter declarations (currently lines 150–174) with:

```csharp
        command.CommandText = @"
            INSERT INTO Products (Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode, Tags,
                                  UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, SellInSecondaryUnit)
            VALUES ($Id, $Name, $Sku, $Category, $Price, $OriginalPrice, $DiscountPercent, $ImagePath, $Barcode, $Tags,
                    $UnitId, $UnitCode, $UnitShortName, $UnitFactor, $IsDivisible, $SellInSecondaryUnit)
            ON CONFLICT(Id) DO UPDATE SET
                Name=excluded.Name,
                Sku=excluded.Sku,
                Category=excluded.Category,
                Price=excluded.Price,
                OriginalPrice=excluded.OriginalPrice,
                DiscountPercent=excluded.DiscountPercent,
                ImagePath=excluded.ImagePath,
                Barcode=excluded.Barcode,
                Tags=excluded.Tags,
                UnitId=excluded.UnitId,
                UnitCode=excluded.UnitCode,
                UnitShortName=excluded.UnitShortName,
                UnitFactor=excluded.UnitFactor,
                IsDivisible=excluded.IsDivisible,
                SellInSecondaryUnit=excluded.SellInSecondaryUnit;
        ";

        var idParam = command.Parameters.Add("$Id", SqliteType.Text);
        var nameParam = command.Parameters.Add("$Name", SqliteType.Text);
        var skuParam = command.Parameters.Add("$Sku", SqliteType.Text);
        var categoryParam = command.Parameters.Add("$Category", SqliteType.Text);
        var priceParam = command.Parameters.Add("$Price", SqliteType.Real);
        var origPriceParam = command.Parameters.Add("$OriginalPrice", SqliteType.Real);
        var discountParam = command.Parameters.Add("$DiscountPercent", SqliteType.Real);
        var imageParam = command.Parameters.Add("$ImagePath", SqliteType.Text);
        var barcodeParam = command.Parameters.Add("$Barcode", SqliteType.Text);
        var tagsParam = command.Parameters.Add("$Tags", SqliteType.Text);
        var unitIdParam = command.Parameters.Add("$UnitId", SqliteType.Text);
        var unitCodeParam = command.Parameters.Add("$UnitCode", SqliteType.Text);
        var unitShortNameParam = command.Parameters.Add("$UnitShortName", SqliteType.Text);
        var unitFactorParam = command.Parameters.Add("$UnitFactor", SqliteType.Real);
        var isDivisibleParam = command.Parameters.Add("$IsDivisible", SqliteType.Integer);
        var sellInUnitParam = command.Parameters.Add("$SellInSecondaryUnit", SqliteType.Integer);
```

and inside the `foreach (var p in products)` loop, after the `tagsParam.Value = ...` line, add:

```csharp
            unitIdParam.Value = p.UnitId ?? string.Empty;
            unitCodeParam.Value = p.UnitCode ?? string.Empty;
            unitShortNameParam.Value = p.UnitShortName ?? string.Empty;
            unitFactorParam.Value = p.UnitFactor;
            isDivisibleParam.Value = p.IsDivisible ? 1 : 0;
            sellInUnitParam.Value = p.SellInSecondaryUnit ? 1 : 0;
```

**3d.** Extend `ReadProduct` (currently lines 195–210) with the new ordinals, and update every `SELECT` over `Products` to match. Replace the body of `ReadProduct` with:

```csharp
    private Product ReadProduct(SqliteDataReader reader)
    {
        return new Product
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Sku = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Category = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Price = reader.GetDecimal(4),
            OriginalPrice = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            DiscountPercent = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            ImagePath = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Barcode = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            TagIds = ReadTags(reader, 9),
            // Rows written before the unit migration have NULL here, so every
            // one of these falls back rather than throwing.
            UnitId = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            UnitCode = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            UnitShortName = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            UnitFactor = reader.IsDBNull(13) ? 0m : reader.GetDecimal(13),
            IsDivisible = !reader.IsDBNull(14) && reader.GetBoolean(14),
            SellInSecondaryUnit = !reader.IsDBNull(15) && reader.GetBoolean(15),
        };
    }
```

Then find every `SELECT` that feeds `ReadProduct` and append the six columns to its column list in the same order. Locate them with:

```bash
cd /c/work/vv-cash && grep -n "FROM Products" src/VvCash/Services/Data/OfflineStorageService.cs
```

For each hit, the column list must become:

```
Id, Name, Sku, Category, Price, OriginalPrice, DiscountPercent, ImagePath, Barcode, Tags, UnitId, UnitCode, UnitShortName, UnitFactor, IsDivisible, SellInSecondaryUnit
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~OfflineStorageServiceTest"
```

Expected: `Passed!`. If a test fails with `Index was outside the bounds of the array`, one of the `SELECT` statements in 3d was missed.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/Data/OfflineStorageService.cs tests/VvCash.Tests/OfflineStorageServiceTest.cs
git commit -m "feat(units): persist the secondary unit in the offline catalogue"
```

---

### Task 5: Entry mode and unit snapshot on CartItem

**Files:**
- Modify: `src/VvCash/Models/CartItem.cs`
- Test: `tests/VvCash.Tests/CartItemUnitTest.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/CartItemUnitTest.cs`:

```csharp
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class CartItemUnitTest
{
    private static Product Tile() => new()
    {
        Id = "p1", Name = "Плитка", Price = 100m,
        UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
        UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
    };

    [Fact]
    public void QuantityInUnitDisplay_DropsTrailingZeros()
    {
        var item = new CartItem { Product = Tile(), Quantity = 53m, QuantityInUnit = 12.720m };

        Assert.Equal("12.72", item.QuantityInUnitDisplay);
    }

    [Fact]
    public void EnteredInUnit_DefaultsToFalse()
    {
        Assert.False(new CartItem { Product = Tile(), Quantity = 1m }.EnteredInUnit);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~CartItemUnitTest"
```

Expected: build error, `'CartItem' does not contain a definition for 'QuantityInUnit'`.

- [ ] **Step 3: Write the implementation**

In `src/VvCash/Models/CartItem.cs`, add to the class (keep the existing `_quantity` field and `QuantityDisplay` untouched):

```csharp
    /// <summary>Which unit the cashier typed this line in. Drives the quantity
    /// pad and how the line reads on screen and on the receipt; it never
    /// affects money, which is always pieces × price.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuantityInUnitDisplay))]
    private bool _enteredInUnit;

    /// <summary>The line's amount in the product's secondary unit.
    ///
    /// Stored rather than derived from Quantity × factor, because for a
    /// divisible product the two differ: 12.5 m² becomes 52.083333 pieces,
    /// which multiplies back to 12.49999992. The server accepts either inside
    /// its tolerance, but the customer must see the 12.5 they asked for. For an
    /// indivisible product the two agree exactly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuantityInUnitDisplay))]
    private decimal _quantityInUnit;

    /// <summary>Amount in the secondary unit without trailing zeros, so a line
    /// reads "12.72" and not "12.720".</summary>
    public string QuantityInUnitDisplay => QuantityInUnit == decimal.Truncate(QuantityInUnit)
        ? decimal.Truncate(QuantityInUnit).ToString(CultureInfo.InvariantCulture)
        : QuantityInUnit.ToString("0.######", CultureInfo.InvariantCulture);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~CartItemUnitTest"
```

Expected: `Passed!` with 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Models/CartItem.cs tests/VvCash.Tests/CartItemUnitTest.cs
git commit -m "feat(units): track the entry unit and unit amount on a cart line"
```

---

### Task 6: SetQuantityInUnit on the cart

**Files:**
- Modify: `src/VvCash/Services/CartService.cs`, `src/VvCash/Services/ICartService.cs`
- Modify: `tests/VvCash.Tests/PosViewModelSellerGateTest.cs:146` (its hand-written `ICartService` stub must gain the new member)
- Test: `tests/VvCash.Tests/CartServiceUnitTest.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/CartServiceUnitTest.cs`:

```csharp
using System;
using System.Collections.Generic;
using VvCash.Models;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CartServiceUnitTest
{
    private static Product Tile(bool sellInUnit = true, bool divisible = false) => new()
    {
        Id = "p1", Name = "Плитка", Price = 100m,
        UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
        UnitFactor = 0.24m, IsDivisible = divisible, SellInSecondaryUnit = sellInUnit,
    };

    private static CartService NewCart() => new(new StubPromotionProvider(Array.Empty<Promotion>()));

    [Fact]
    public void SetQuantityInUnit_ConvertsToPiecesAndKeepsTheUnitAmount()
    {
        var c = NewCart();
        c.AddProduct(Tile());

        c.SetQuantityInUnit(c.Items[0], 12.5m);

        Assert.Equal(53m, c.Items[0].Quantity);
        Assert.Equal(12.72m, c.Items[0].QuantityInUnit);
        // Money is always pieces × price: 53 tiles at 100.
        Assert.Equal(5300m, c.Items[0].LineTotal);
    }

    [Fact]
    public void SetQuantityInUnit_RemovesTheLineAtZero()
    {
        var c = NewCart();
        c.AddProduct(Tile());

        c.SetQuantityInUnit(c.Items[0], 0m);

        Assert.Empty(c.Items);
    }

    [Fact]
    public void SetQuantityInUnit_IsIgnored_ForAPieceOnlyProduct()
    {
        // Nothing to convert with, and silently inventing a factor would bill
        // the customer for a quantity nobody entered.
        var c = NewCart();
        c.AddProduct(new Product { Id = "p2", Name = "Товар", Price = 10m });

        c.SetQuantityInUnit(c.Items[0], 12.5m);

        Assert.Equal(1m, c.Items[0].Quantity);
        Assert.Equal(0m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void AddProduct_TakesTheEntryModeFromTheProductCard()
    {
        var c = NewCart();

        c.AddProduct(Tile(sellInUnit: true));

        Assert.True(c.Items[0].EnteredInUnit);
        // One piece, not one m2: a tap adds a piece and the pad refines it.
        Assert.Equal(1m, c.Items[0].Quantity);
        Assert.Equal(0.24m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void AddProduct_StaysInPieces_WhenTheCardSaysSo()
    {
        var c = NewCart();

        c.AddProduct(Tile(sellInUnit: false));

        Assert.False(c.Items[0].EnteredInUnit);
    }

    [Fact]
    public void IncreaseQuantity_StepsByOnePiece_EvenInUnitMode()
    {
        // "+" on a tile adds a tile, not a square metre.
        var c = NewCart();
        c.AddProduct(Tile());
        c.SetQuantityInUnit(c.Items[0], 12.5m);

        c.IncreaseQuantity(c.Items[0]);

        Assert.Equal(54m, c.Items[0].Quantity);
        Assert.Equal(12.96m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void DecreaseQuantity_RecomputesTheUnitAmount()
    {
        var c = NewCart();
        c.AddProduct(Tile());
        c.SetQuantityInUnit(c.Items[0], 12.5m);

        c.DecreaseQuantity(c.Items[0]);

        Assert.Equal(52m, c.Items[0].Quantity);
        Assert.Equal(12.48m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void SetQuantity_InPieces_RecomputesTheUnitAmount()
    {
        var c = NewCart();
        c.AddProduct(Tile());

        c.SetQuantity(c.Items[0], 10m);

        Assert.Equal(10m, c.Items[0].Quantity);
        Assert.Equal(2.4m, c.Items[0].QuantityInUnit);
    }

    [Fact]
    public void AddProduct_Twice_KeepsTheUnitAmountInStep()
    {
        var c = NewCart();
        var tile = Tile();

        c.AddProduct(tile);
        c.AddProduct(tile);

        Assert.Equal(2m, c.Items[0].Quantity);
        Assert.Equal(0.48m, c.Items[0].QuantityInUnit);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~CartServiceUnitTest"
```

Expected: build error, `'CartService' does not contain a definition for 'SetQuantityInUnit'`.

- [ ] **Step 3: Write the implementation**

**3a.** In `src/VvCash/Services/CartService.cs`, replace `AddProduct` (currently lines 114–126) with:

```csharp
    public void AddProduct(Product product)
    {
        var existing = _items.FirstOrDefault(i => i.Product.Id == product.Id);
        if (existing != null)
        {
            existing.Quantity++;
            SyncUnitAmount(existing);
        }
        else
        {
            var item = new CartItem
            {
                Product = product,
                Quantity = 1,
                // A tap adds one piece; the quantity pad is where the cashier
                // states the real amount. The entry mode comes from the product
                // card so tiles open in m² and rolls in pieces.
                EnteredInUnit = product.SellInSecondaryUnit && product.HasSecondaryUnit,
            };
            SyncUnitAmount(item);
            _items.Add(item);
        }
        RaiseCartChanged();
    }
```

**3b.** Replace `IncreaseQuantity` and `DecreaseQuantity` (currently lines 134–151) with:

```csharp
    /// <summary>Steps by one piece regardless of the entry unit: "+" on a tile
    /// adds a tile, not a square metre. Nobody sells a loose square metre.</summary>
    public void IncreaseQuantity(CartItem item)
    {
        item.Quantity++;
        SyncUnitAmount(item);
        RaiseCartChanged();
    }

    public void DecreaseQuantity(CartItem item)
    {
        if (item.Quantity > 1m)
        {
            item.Quantity--;
            SyncUnitAmount(item);
            RaiseCartChanged();
        }
        else
        {
            RemoveItem(item);
        }
    }
```

**3c.** Replace `SetQuantity` (currently lines 153–165) with the version below and add `SetQuantityInUnit` and `SyncUnitAmount` after it:

```csharp
    /// <summary>Sets an exact quantity in pieces — the entry point for weighted
    /// goods, where the amount comes from a scale rather than from +/- taps. A
    /// non-positive quantity removes the line, matching what DecreaseQuantity
    /// does at zero.</summary>
    public void SetQuantity(CartItem item, decimal quantity)
    {
        if (quantity <= 0m)
        {
            RemoveItem(item);
            return;
        }
        item.Quantity = quantity;
        SyncUnitAmount(item);
        RaiseCartChanged();
    }

    /// <summary>Sets the line from an amount in the product's secondary unit —
    /// "12.5 m² of tile" rather than "53 tiles".
    ///
    /// A piece-only product is left alone: there is no factor to convert with,
    /// and inventing one would bill a quantity nobody entered.</summary>
    public void SetQuantityInUnit(CartItem item, decimal amountInUnit)
    {
        if (!item.Product.HasSecondaryUnit) return;

        if (amountInUnit <= 0m)
        {
            // RemoveItem raises CartChanged itself; raising again here would
            // re-price the cart twice per keystroke.
            RemoveItem(item);
            return;
        }

        var (quantity, quantityInUnit) = UnitConverter.ToBase(
            amountInUnit, item.Product.UnitFactor, item.Product.IsDivisible);

        item.Quantity = quantity;
        item.QuantityInUnit = quantityInUnit;
        RaiseCartChanged();
    }

    /// <summary>Brings the unit amount back in line after the piece count moved
    /// on its own — a +/- tap, or a quantity set in pieces. Only ever called
    /// where pieces are authoritative, so recomputing is exactly right; a line
    /// set from a unit amount keeps the figure the cashier typed instead.</summary>
    private static void SyncUnitAmount(CartItem item)
    {
        item.QuantityInUnit = item.Product.HasSecondaryUnit
            ? UnitConverter.ToUnit(item.Quantity, item.Product.UnitFactor)
            : 0m;
    }
```

**3d.** In `src/VvCash/Services/ICartService.cs`, add after the existing `SetQuantity` declaration (line 45):

```csharp
    void SetQuantityInUnit(CartItem item, decimal amountInUnit);
```

**3e.** In `tests/VvCash.Tests/PosViewModelSellerGateTest.cs`, the hand-written `ICartService` stub around line 146 will no longer compile. Add next to its existing `SetQuantity`:

```csharp
        public void SetQuantityInUnit(CartItem item, decimal amountInUnit) { }
```

- [ ] **Step 4: Run the full suite to verify it passes**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1"
```

Expected: `Passed!`, everything green including the pre-existing `CartServiceQuoteTest`.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Services/CartService.cs src/VvCash/Services/ICartService.cs tests/VvCash.Tests/CartServiceUnitTest.cs tests/VvCash.Tests/PosViewModelSellerGateTest.cs
git commit -m "feat(units): set a cart line from an amount in the secondary unit"
```

---

### Task 7: Send the unit trio on the sale document

**Files:**
- Modify: `src/VvCash/Models/Api/DocumentRequest.cs`
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:1676-1684`
- Test: `tests/VvCash.Tests/DocumentProductUnitTest.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/DocumentProductUnitTest.cs`:

```csharp
using System.Text.Json;
using VvCash.Models.Api;
using Xunit;

namespace VvCash.Tests;

// The server takes unit_id, unit_factor and quantity_in_unit together or not at
// all, and rejects the document on a partial trio. These tests pin the two
// shapes that may go on the wire.
public class DocumentProductUnitTest
{
    private static string Serialize(DocumentProduct p) => JsonSerializer.Serialize(p);

    [Fact]
    public void ProductLine_OmitsTheWholeTrio_WhenSoldByThePiece()
    {
        var json = Serialize(new DocumentProduct { ProductId = "p1", Quantity = 2m, SellPrice = 10m });

        Assert.DoesNotContain("unit_id", json);
        Assert.DoesNotContain("unit_factor", json);
        Assert.DoesNotContain("quantity_in_unit", json);
    }

    [Fact]
    public void ProductLine_CarriesTheWholeTrio_WhenSoldInAUnit()
    {
        var json = Serialize(new DocumentProduct
        {
            ProductId = "p1",
            Quantity = 53m,
            SellPrice = 100m,
            UnitId = "u-1",
            UnitFactor = 0.24m,
            QuantityInUnit = 12.72m,
        });

        Assert.Contains("\"unit_id\":\"u-1\"", json);
        Assert.Contains("\"unit_factor\":0.24", json);
        Assert.Contains("\"quantity_in_unit\":12.72", json);
        // quantity stays pieces either way.
        Assert.Contains("\"quantity\":53", json);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~DocumentProductUnitTest"
```

Expected: build error, `'DocumentProduct' does not contain a definition for 'UnitId'`.

- [ ] **Step 3: Write the implementation**

**3a.** In `src/VvCash/Models/Api/DocumentRequest.cs`, append to the `DocumentProduct` class after `DiscountPercent`:

```csharp
    /// <summary>Unit snapshot: which unit the operator typed in, the factor in
    /// force at the till, and what the line came to in that unit. The server
    /// takes all three or none and rejects a partial trio, so these are set
    /// together or left null together.
    ///
    /// <see cref="Quantity"/> stays in pieces regardless — the trio records how
    /// the amount was entered, not what was sold.
    ///
    /// The factor sent is the one this register synced, not one recomputed at
    /// sale time: the register may have been offline when the card changed, its
    /// receipt is already printed, and the server trusts a cash sale's own
    /// factor for exactly that reason.</summary>
    [JsonPropertyName("unit_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnitId { get; set; }

    [JsonPropertyName("unit_factor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? UnitFactor { get; set; }

    [JsonPropertyName("quantity_in_unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? QuantityInUnit { get; set; }
```

**3b.** In `src/VvCash/ViewModels/PosViewModel.cs`, replace the `return new DocumentProduct { ... }` block (currently lines 1676–1684) with:

```csharp
                            return new DocumentProduct
                            {
                                Name = item.Product.Name,
                                ProductId = item.Product.Id,
                                Quantity = item.Quantity,
                                SellPrice = item.Product.Price,
                                PriceBeforeDiscount = before,
                                DiscountPercent = pct,
                                // All three or none: the server rejects a partial trio.
                                UnitId = item.Product.HasSecondaryUnit ? item.Product.UnitId : null,
                                UnitFactor = item.Product.HasSecondaryUnit ? item.Product.UnitFactor : null,
                                QuantityInUnit = item.Product.HasSecondaryUnit ? item.QuantityInUnit : null,
                            };
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~DocumentProductUnitTest"
```

Expected: `Passed!` with 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Models/Api/DocumentRequest.cs src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/DocumentProductUnitTest.cs
git commit -m "feat(units): send the unit snapshot on the sale document"
```

---

### Task 8: Show the unit amount on the receipt

**Files:**
- Modify: `src/VvCash/Services/Hardware/EscPosPrinterService.cs:53`, `:97`
- Test: `tests/VvCash.Tests/EscPosUnitTest.cs` (create)

`PrintReceiptAsync` builds its bytes inline and sends them in the same method, so nothing can assert on the sale receipt today. The return receipt in the same file already solves this: `EscPosPrinterService.BuildReturnReceipt` is a static byte-array builder and `EscPosReturnTest` asserts on `Encoding.UTF8.GetString(bytes)`. Extract the sale receipt the same way — it follows the file's own pattern and is the seam this task needs.

- [ ] **Step 1: Extract the sale receipt into a static builder**

In `src/VvCash/Services/Hardware/EscPosPrinterService.cs`, split `PrintReceiptAsync` (currently lines 39–84) so the byte building becomes static and the method only sends:

```csharp
    /// <summary>Builds the sale receipt bytes. Static and separate from sending
    /// so the layout can be asserted on, exactly as BuildReturnReceipt is.</summary>
    public static byte[] BuildSaleReceipt(
        IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total,
        string? discountName = null)
    {
        using var ms = new MemoryStream();
        Write(ms, CmdInit);
        Write(ms, CmdAlignCenter);
        Write(ms, CmdDoubleSizeOn);
        WriteLine(ms, "VV CASH POS");
        Write(ms, CmdDoubleSizeOff);
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignLeft);
        foreach (var item in items)
        {
            var line = $"{item.Product.Name} x{item.QuantityDisplay}";
            var price = $"${item.LineTotal:F2}";
            WriteLine(ms, PadLine(line, price, 32));
        }
        WriteLine(ms, "----------------------------");
        WriteLine(ms, PadLine("Subtotal:", $"${subtotal:F2}", 32));
        if (discount > 0)
        {
            WriteLine(ms, PadLine("Discount:", $"-${discount:F2}", 32));
            if (!string.IsNullOrWhiteSpace(discountName))
                WriteLine(ms, Truncate(discountName!, 32));
        }

        Write(ms, CmdBoldOn);
        WriteLine(ms, PadLine("TOTAL:", $"${total:F2}", 32));
        Write(ms, CmdBoldOff);
        WriteLine(ms, "----------------------------");
        Write(ms, CmdAlignCenter);
        WriteLine(ms, "Thank you for shopping!");
        Write(ms, CmdLineFeed);
        Write(ms, CmdLineFeed);
        Write(ms, CmdCut);
        return ms.ToArray();
    }

    public async Task<bool> PrintReceiptAsync(IEnumerable<CartItem> items, decimal subtotal, decimal discount, decimal total, IEnumerable<Coupon> coupons, string? discountName = null)
    {
        try
        {
            await SendAsync(BuildSaleReceipt(items, subtotal, discount, total, discountName));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Print error: {ex.Message}");
            SetStatus(PrinterStatus.Error);
            return false;
        }
    }
```

If `Write`, `WriteLine`, `PadLine` or `Truncate` are instance members, make them `static` — `BuildReturnReceipt` is already static and must be using them, so they almost certainly are already.

Verify nothing else broke:

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~EscPos"
```

Expected: `Passed!` — a pure refactor, no behaviour change yet. Commit it on its own:

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs
git commit -m "refactor(printing): extract the sale receipt into a static builder"
```

- [ ] **Step 2: Write the failing test**

Create `tests/VvCash.Tests/EscPosUnitTest.cs`:

```csharp
using System.Collections.Generic;
using System.Text;
using VvCash.Models;
using VvCash.Services.Hardware;
using Xunit;

namespace VvCash.Tests;

public class EscPosUnitTest
{
    private static CartItem TileLine() => new()
    {
        Product = new Product
        {
            Id = "p1", Name = "Плитка", Price = 100m,
            UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
            UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
        },
        Quantity = 53m,
        QuantityInUnit = 12.72m,
        EnteredInUnit = true,
    };

    private static string Render(IEnumerable<CartItem> items) =>
        Encoding.UTF8.GetString(
            EscPosPrinterService.BuildSaleReceipt(items, subtotal: 5300m, discount: 0m, total: 5300m));

    [Fact]
    public void Receipt_ShowsTheUnitAmount_ForAUnitLine()
    {
        // The customer asked for square metres and pays for whole tiles; the
        // receipt has to show both or the rounding looks like a mistake.
        var text = Render(new[] { TileLine() });

        Assert.Contains("12.72 м²", text);
        Assert.Contains("Плитка x53", text);
    }

    [Fact]
    public void Receipt_IsUnchanged_ForAPieceOnlyLine()
    {
        var line = new CartItem { Product = new Product { Id = "p2", Name = "Товар", Price = 10m }, Quantity = 2m };

        var text = Render(new[] { line });

        Assert.DoesNotContain("м²", text);
        Assert.Contains("Товар x2", text);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~EscPosUnitTest"
```

Expected: `Assert.Contains() Failure` — the rendered text has no `12.72 м²`.

- [ ] **Step 4: Write the implementation**

In `src/VvCash/Services/Hardware/EscPosPrinterService.cs`, inside `BuildSaleReceipt`'s `foreach (var item in items)` loop, after the existing `WriteLine(ms, PadLine(line, price, 32));`, add:

```csharp
            // A unit line prints both figures: the customer asked for square
            // metres and is billed for whole tiles, and showing only one of the
            // two makes the round-up look like an error.
            if (item.Product.HasSecondaryUnit)
                WriteLine(ms, $"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}");
```

And in `PrintPreReceiptAsync`'s loop (currently line 96), after the existing `WriteLine`:

```csharp
                if (item.Product.HasSecondaryUnit)
                    WriteLine(ms, $"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}");
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~EscPos"
```

Expected: `Passed!`, including the pre-existing `EscPosReturnTest` and `EscPosExchangeTest`.

- [ ] **Step 6: Commit**

```bash
git add src/VvCash/Services/Hardware/EscPosPrinterService.cs tests/VvCash.Tests/EscPosUnitTest.cs
git commit -m "feat(units): print the unit amount alongside the piece count"
```

---

### Task 9: Keep the entry mode across park and unpark

**Files:**
- Modify: `src/VvCash/Models/ParkedSaleSnapshot.cs:29-33`
- Modify: `src/VvCash/ViewModels/PosViewModel.cs:1389`, `:1582`
- Test: `tests/VvCash.Tests/ParkedSaleUnitTest.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/ParkedSaleUnitTest.cs`:

```csharp
using System.Text.Json;
using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class ParkedSaleUnitTest
{
    [Fact]
    public void ParkedCartItem_RoundTripsTheEntryModeAndUnitAmount()
    {
        // A line parked in m² must come back in m². Restoring it in pieces
        // would silently change what the cashier sees on resume.
        var original = new ParkedCartItem
        {
            Product = new Product
            {
                Id = "p1", Name = "Плитка", Price = 100m,
                UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
                UnitFactor = 0.24m, IsDivisible = false, SellInSecondaryUnit = true,
            },
            Quantity = 53m,
            QuantityInUnit = 12.72m,
            EnteredInUnit = true,
        };

        var restored = JsonSerializer.Deserialize<ParkedCartItem>(JsonSerializer.Serialize(original))!;

        Assert.Equal(53m, restored.Quantity);
        Assert.Equal(12.72m, restored.QuantityInUnit);
        Assert.True(restored.EnteredInUnit);
        Assert.Equal("u-1", restored.Product.UnitId);
        Assert.Equal(0.24m, restored.Product.UnitFactor);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~ParkedSaleUnitTest"
```

Expected: build error, `'ParkedCartItem' does not contain a definition for 'QuantityInUnit'`.

- [ ] **Step 3: Write the implementation**

**3a.** In `src/VvCash/Models/ParkedSaleSnapshot.cs`, extend `ParkedCartItem`:

```csharp
public class ParkedCartItem
{
    public Product Product { get; set; } = null!;
    public decimal Quantity { get; set; }

    /// <summary>Unit entry carried across park and unpark. A line parked in m²
    /// must come back in m²: restoring it in pieces would silently change what
    /// the cashier sees when the sale resumes.</summary>
    public decimal QuantityInUnit { get; set; }
    public bool EnteredInUnit { get; set; }
}
```

**3b.** In `src/VvCash/ViewModels/PosViewModel.cs` line 1389, extend the park projection:

```csharp
            .Select(i => new ParkedCartItem
            {
                Product = i.Product,
                Quantity = i.Quantity,
                QuantityInUnit = i.QuantityInUnit,
                EnteredInUnit = i.EnteredInUnit,
            })
```

**3c.** At line 1582, extend the unpark projection:

```csharp
            .Select(i => new CartItem
            {
                Product = i.Product,
                Quantity = i.Quantity,
                QuantityInUnit = i.QuantityInUnit,
                EnteredInUnit = i.EnteredInUnit,
            })
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~ParkedSaleUnitTest"
```

Expected: `Passed!` with 1 test.

- [ ] **Step 5: Commit**

```bash
git add src/VvCash/Models/ParkedSaleSnapshot.cs src/VvCash/ViewModels/PosViewModel.cs tests/VvCash.Tests/ParkedSaleUnitTest.cs
git commit -m "feat(units): keep the entry unit across park and unpark"
```

---

### Task 10: The quantity pad

Everything above ships without a single visible change. This is where the feature appears. There is no quantity pad in the app today — `SetQuantity` has never had a UI caller — so this builds one.

**Files:**
- Modify: `src/VvCash/ViewModels/PosViewModel.cs` (modal state and commands)
- Modify: `src/VvCash/Views/PosView.axaml` (cart line + new modal)
- Modify: `src/VvCash/Views/CustomerDisplayWindow.axaml:38`
- Test: `tests/VvCash.Tests/QuantityPadTest.cs` (create)

The view model logic is testable; the XAML is not. Test the first, eyeball the second.

- [ ] **Step 1: Write the failing test**

Create `tests/VvCash.Tests/QuantityPadTest.cs`:

```csharp
using VvCash.Models;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

// The pad's whole job is showing the cashier what a typed amount becomes before
// it is committed — above all the round-up on indivisible goods, which changes
// what the customer pays.
public class QuantityPadTest
{
    private static Product Tile(bool divisible = false) => new()
    {
        Id = "p1", Name = "Плитка", Price = 100m,
        UnitId = "u-1", UnitCode = "m2", UnitShortName = "м²",
        UnitFactor = 0.24m, IsDivisible = divisible, SellInSecondaryUnit = true,
    };

    private static QuantityPadViewModel PadFor(Product p, bool inUnit = true) =>
        new(new CartItem { Product = p, Quantity = 1m, EnteredInUnit = inUnit });

    [Fact]
    public void Preview_ShowsThePieceCountAndTheRoundedUnitAmount()
    {
        var pad = PadFor(Tile());

        pad.Input = "12.5";

        Assert.Equal(53m, pad.PreviewQuantity);
        Assert.Equal(12.72m, pad.PreviewQuantityInUnit);
        Assert.Equal(5300m, pad.PreviewTotal);
        Assert.True(pad.IsRounded);
    }

    [Fact]
    public void Preview_DoesNotFlagRounding_OnAnExactMultiple()
    {
        var pad = PadFor(Tile());

        pad.Input = "12";

        Assert.Equal(50m, pad.PreviewQuantity);
        Assert.False(pad.IsRounded);
    }

    [Fact]
    public void Preview_KeepsTheTypedAmount_ForADivisibleProduct()
    {
        var pad = PadFor(Tile(divisible: true));

        pad.Input = "12.5";

        Assert.Equal(12.5m, pad.PreviewQuantityInUnit);
        Assert.False(pad.IsRounded);
    }

    [Fact]
    public void Preview_InPieceMode_ReportsTheUnitAmount()
    {
        var pad = PadFor(Tile(), inUnit: false);

        pad.Input = "10";

        Assert.Equal(10m, pad.PreviewQuantity);
        Assert.Equal(2.4m, pad.PreviewQuantityInUnit);
    }

    [Fact]
    public void PieceMode_RejectsAFractionalCount_ForAnIndivisibleProduct()
    {
        var pad = PadFor(Tile(), inUnit: false);

        pad.Input = "10.5";

        Assert.False(pad.IsValid);
    }

    [Fact]
    public void UnitPrice_FollowsTheSelectedUnit()
    {
        var pad = PadFor(Tile());

        Assert.Equal(416.67m, decimal.Round(pad.UnitPrice, 2));
        Assert.Equal("м²", pad.UnitLabel);

        pad.EnteredInUnit = false;

        Assert.Equal(100m, pad.UnitPrice);
        Assert.Equal("шт", pad.UnitLabel);
    }

    [Fact]
    public void EmptyOrGarbageInput_IsNotCommittable()
    {
        var pad = PadFor(Tile());

        pad.Input = "";
        Assert.False(pad.IsValid);

        pad.Input = "abc";
        Assert.False(pad.IsValid);

        pad.Input = "0";
        Assert.False(pad.IsValid);
    }

    [Fact]
    public void PieceOnlyProduct_HasNoUnitToggle()
    {
        var pad = PadFor(new Product { Id = "p2", Name = "Товар", Price = 10m }, inUnit: false);

        Assert.False(pad.CanSwitchUnit);
        Assert.Equal("шт", pad.UnitLabel);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~QuantityPadTest"
```

Expected: build error, `The type or namespace name 'QuantityPadViewModel' could not be found`.

- [ ] **Step 3: Write the view model**

Create `src/VvCash/ViewModels/QuantityPadViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VvCash.Models;
using VvCash.Services;

namespace VvCash.ViewModels;

/// <summary>Backs the quantity pad: the cashier types an amount, and the pad
/// shows what it becomes before anything is committed.
///
/// The live preview exists for one reason. An indivisible product rounds up to
/// the next whole piece, so 12.5 m² of tile bills as 12.72 m². That is the
/// customer's money, and it must be on screen before the line is confirmed, not
/// discovered on the receipt.</summary>
public partial class QuantityPadViewModel : ObservableObject
{
    private readonly CartItem _item;

    public QuantityPadViewModel(CartItem item)
    {
        _item = item;
        _enteredInUnit = item.EnteredInUnit && item.Product.HasSecondaryUnit;
        _input = _enteredInUnit
            ? item.QuantityInUnit.ToString(CultureInfo.InvariantCulture)
            : item.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    public CartItem Item => _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewQuantity), nameof(PreviewQuantityInUnit),
        nameof(PreviewTotal), nameof(PreviewText), nameof(IsRounded), nameof(IsValid))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPrice), nameof(UnitLabel), nameof(PreviewQuantity),
        nameof(PreviewQuantityInUnit), nameof(PreviewTotal), nameof(PreviewText),
        nameof(IsRounded), nameof(IsValid))]
    private bool _enteredInUnit;

    /// <summary>Whether the piece/unit toggle is offered at all. A piece-only
    /// product has nothing to switch to.</summary>
    public bool CanSwitchUnit => _item.Product.HasSecondaryUnit;

    public string UnitLabel => EnteredInUnit ? _item.Product.UnitShortName : "шт";

    /// <summary>Price in whichever unit is selected, so the ticket reads
    /// "416.67 / м²" while the cashier is typing square metres.</summary>
    public decimal UnitPrice => EnteredInUnit
        ? _item.Product.Price / _item.Product.UnitFactor
        : _item.Product.Price;

    private decimal? Parsed =>
        decimal.TryParse(Input, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0m
            ? v
            : null;

    /// <summary>Whether the current input can be committed. Rejects an empty or
    /// unparseable box, a non-positive amount, and a fractional piece count on
    /// an indivisible product — half a tile does not exist.</summary>
    public bool IsValid
    {
        get
        {
            var amount = Parsed;
            if (amount is null) return false;
            if (!EnteredInUnit && !_item.Product.IsDivisible && amount != decimal.Truncate(amount.Value))
                return false;
            return true;
        }
    }

    public decimal PreviewQuantity
    {
        get
        {
            var amount = Parsed;
            if (amount is null) return 0m;
            if (!EnteredInUnit) return amount.Value;
            return UnitConverter.ToBase(
                amount.Value, _item.Product.UnitFactor, _item.Product.IsDivisible).Quantity;
        }
    }

    public decimal PreviewQuantityInUnit
    {
        get
        {
            var amount = Parsed;
            if (amount is null || !_item.Product.HasSecondaryUnit) return 0m;
            if (!EnteredInUnit) return UnitConverter.ToUnit(amount.Value, _item.Product.UnitFactor);
            return UnitConverter.ToBase(
                amount.Value, _item.Product.UnitFactor, _item.Product.IsDivisible).QuantityInUnit;
        }
    }

    public decimal PreviewTotal => PreviewQuantity * _item.Product.Price;

    /// <summary>Whether the entered amount was rounded up to a whole piece.
    /// Drives the callout in the pad, because this is the case where the
    /// customer pays for more than they asked for.</summary>
    public bool IsRounded
    {
        get
        {
            var amount = Parsed;
            if (amount is null || !EnteredInUnit) return false;
            return PreviewQuantityInUnit != amount.Value;
        }
    }

    public string PreviewText => _item.Product.HasSecondaryUnit
        ? $"→ {PreviewQuantity} шт = {PreviewQuantityInUnit} {_item.Product.UnitShortName} · {PreviewTotal:F2}"
        : $"→ {PreviewQuantity} шт · {PreviewTotal:F2}";

    public void Append(string digit) => Input += digit;

    public void Backspace()
    {
        if (Input.Length > 0) Input = Input[..^1];
    }

    public void Clear() => Input = string.Empty;

    /// <summary>Writes the pad's result back through the cart, which is what
    /// recomputes totals and re-prices the cart.</summary>
    public void Commit(ICartService cart)
    {
        var amount = Parsed;
        if (amount is null || !IsValid) return;

        _item.EnteredInUnit = EnteredInUnit;
        if (EnteredInUnit) cart.SetQuantityInUnit(_item, amount.Value);
        else cart.SetQuantity(_item, amount.Value);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1 --filter FullyQualifiedName~QuantityPadTest"
```

Expected: `Passed!` with 8 tests.

- [ ] **Step 5: Commit the view model**

```bash
git add src/VvCash/ViewModels/QuantityPadViewModel.cs tests/VvCash.Tests/QuantityPadTest.cs
git commit -m "feat(units): quantity pad view model with a live conversion preview"
```

- [ ] **Step 6: Wire the modal into PosViewModel**

In `src/VvCash/ViewModels/PosViewModel.cs`, next to the existing modal flags (around lines 145–246) add:

```csharp
    [ObservableProperty] private bool _isQuantityPadVisible = false;
    [ObservableProperty] private QuantityPadViewModel? _quantityPad;
```

and next to the other modal commands (follow `OpenDiscountModalCommand` / `CloseDiscountModalCommand` at lines 1261–1267 for the exact `[RelayCommand]` idiom in this file):

```csharp
    [RelayCommand]
    private void OpenQuantityPad(CartItem item)
    {
        QuantityPad = new QuantityPadViewModel(item);
        IsQuantityPadVisible = true;
    }

    [RelayCommand]
    private void CloseQuantityPad()
    {
        IsQuantityPadVisible = false;
        QuantityPad = null;
    }

    [RelayCommand]
    private void ConfirmQuantityPad()
    {
        QuantityPad?.Commit(_cartService);
        CloseQuantityPad();
    }
```

- [ ] **Step 7: Build to check it compiles**

```bash
cd /c/work/vv-cash && dotnet build src/VvCash/VvCash.csproj -o build/verify
```

Expected: `Build succeeded`.

- [ ] **Step 8: Make the cart line open the pad and show both amounts**

In `src/VvCash/Views/PosView.axaml`, replace the quantity `TextBlock` at line 456 with a button that opens the pad:

```xml
                                                    <Button Classes="QtyButton" MinWidth="26" Height="30" Padding="4,0" CornerRadius="8"
                                                            Command="{Binding DataContext.OpenQuantityPadCommand, ElementName=RootWindow}"
                                                            CommandParameter="{Binding}">
                                                        <TextBlock Text="{Binding QuantityDisplay}" TextAlignment="Center" VerticalAlignment="Center"
                                                                   Foreground="{StaticResource Slate900Brush}" FontWeight="Bold" FontSize="15"/>
                                                    </Button>
```

Bind the command through `ElementName=RootWindow`, exactly as the neighbouring `+`/`−` buttons do. Do not cast an ancestor `DataContext` to the view-model type — it compiles and then crashes at runtime.

In the same template, under the SKU `TextBlock` at line 448, add the unit amount:

```xml
                                                    <TextBlock IsVisible="{Binding Product.HasSecondaryUnit}"
                                                               FontSize="12" Foreground="{StaticResource Slate500Brush}">
                                                        <TextBlock.Text>
                                                            <MultiBinding StringFormat="{}{0} {1}">
                                                                <Binding Path="QuantityInUnitDisplay"/>
                                                                <Binding Path="Product.UnitShortName"/>
                                                            </MultiBinding>
                                                        </TextBlock.Text>
                                                    </TextBlock>
```

- [ ] **Step 9: Add the modal**

In `src/VvCash/Views/PosView.axaml`, in the `<!-- ======================= Modals ======================= -->` section (line 636 onward), add a new modal after the Manual Discount Modal. Copy the outer `Border`/overlay structure from the Manual Discount Modal at lines 638–720 verbatim — same overlay brush, same corner radius, same close-button placement — and put this inside it:

```xml
        <!-- Quantity Pad Modal -->
        <Border IsVisible="{Binding IsQuantityPadVisible}">
            <StackPanel Spacing="12" Width="320">
                <TextBlock Text="{Binding QuantityPad.Item.Product.Name}" FontSize="18" FontWeight="Bold"
                           Foreground="{StaticResource Slate900Brush}" TextTrimming="CharacterEllipsis"/>

                <!-- price in the selected unit -->
                <TextBlock FontSize="14" Foreground="{StaticResource Slate500Brush}">
                    <TextBlock.Text>
                        <MultiBinding StringFormat="{}{0:F2} / {1}">
                            <Binding Path="QuantityPad.UnitPrice"/>
                            <Binding Path="QuantityPad.UnitLabel"/>
                        </MultiBinding>
                    </TextBlock.Text>
                </TextBlock>

                <!-- unit toggle, hidden for a piece-only product -->
                <ToggleSwitch IsVisible="{Binding QuantityPad.CanSwitchUnit}"
                              IsChecked="{Binding QuantityPad.EnteredInUnit, Mode=TwoWay}"
                              OnContent="{Binding QuantityPad.Item.Product.UnitShortName}"
                              OffContent="шт"/>

                <TextBox Text="{Binding QuantityPad.Input, Mode=TwoWay}" FontSize="26" FontWeight="Bold"
                         TextAlignment="Center" IsReadOnly="True"/>

                <!-- live preview: the round-up has to be visible before OK -->
                <TextBlock Text="{Binding QuantityPad.PreviewText}" FontSize="15" FontWeight="SemiBold"
                           TextWrapping="Wrap" HorizontalAlignment="Center"
                           Foreground="{StaticResource Slate700Brush}"/>
                <TextBlock IsVisible="{Binding QuantityPad.IsRounded}"
                           Text="Округлено вверх до целой штуки" FontSize="12"
                           HorizontalAlignment="Center" Foreground="{StaticResource Red600Brush}"/>

                <UniformGrid Columns="3" Rows="4">
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="1"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="1"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="2"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="2"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="3"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="3"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="4"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="4"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="5"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="5"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="6"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="6"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="7"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="7"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="8"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="8"/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="9"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="9"/>
                    <!-- The decimal separator is always "." because the pad parses
                         with InvariantCulture; a comma would fail to parse and
                         silently disable OK. -->
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="."
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="."/>
                    <Button Classes="QtyButton" Height="52" Margin="3" Content="0"
                            Command="{Binding QuantityPadAppendCommand}" CommandParameter="0"/>
                    <Button Classes="QtyButton" Height="52" Margin="3"
                            Command="{Binding QuantityPadBackspaceCommand}">
                        <material:MaterialIcon Kind="Backspace" Width="20" Height="20"/>
                    </Button>
                </UniformGrid>

                <Grid ColumnDefinitions="*,*">
                    <Button Grid.Column="0" Classes="SecondaryButton" Content="Отмена" Margin="0,0,6,0"
                            HorizontalAlignment="Stretch" Command="{Binding CloseQuantityPadCommand}"/>
                    <Button Grid.Column="1" Classes="PrimaryButton" Content="ОК"
                            HorizontalAlignment="Stretch"
                            IsEnabled="{Binding QuantityPad.IsValid}"
                            Command="{Binding ConfirmQuantityPadCommand}"/>
                </Grid>
            </StackPanel>
        </Border>
```

The pad's buttons bind straight to `{Binding ...}` with no `ElementName`: unlike the cart-line template, the modal sits at the root of `PosView` where the `DataContext` is already `PosViewModel` — the same way the existing `CloseDiscountModalCommand` binds at line 661.

The keypad commands forward to the pad. Add them alongside the pad commands from Step 6:

```csharp
    [RelayCommand]
    private void QuantityPadAppend(string digit) => QuantityPad?.Append(digit);

    [RelayCommand]
    private void QuantityPadBackspace() => QuantityPad?.Backspace();
```

- [ ] **Step 10: Mirror the unit amount on the customer display**

In `src/VvCash/Views/CustomerDisplayWindow.axaml`, after the quantity `TextBlock` at line 38, add:

```xml
                                        <TextBlock IsVisible="{Binding Product.HasSecondaryUnit}"
                                                   FontSize="14" Foreground="#a0a0b8">
                                            <TextBlock.Text>
                                                <MultiBinding StringFormat="{}{0} {1}">
                                                    <Binding Path="QuantityInUnitDisplay"/>
                                                    <Binding Path="Product.UnitShortName"/>
                                                </MultiBinding>
                                            </TextBlock.Text>
                                        </TextBlock>
```

- [ ] **Step 11: Run the app and check it by hand**

```bash
cd /c/work/vv-cash && dotnet run --project src/VvCash/VvCash.csproj
```

Walk through all of it:
1. A piece-only product looks and behaves exactly as before — no unit line, no toggle in the pad.
2. A product with a secondary unit shows the unit amount under the SKU.
3. Tapping the quantity opens the pad in the unit set by `SellInSecondaryUnit`.
4. Typing `12.5` on an indivisible product previews `→ 53 шт = 12.72 м² · 5300.00` and shows the round-up callout.
5. Flipping the toggle to `шт` changes the price line to `100.00 / шт`; typing `10.5` disables OK.
6. Confirming updates the cart line, the total, and the customer display.
7. `+`/`−` step by one piece and the unit amount follows.
8. Parking and unparking the sale keeps the line in m².

- [ ] **Step 12: Run the full suite**

```bash
cd /c/work/vv-cash && powershell -NoProfile -Command "& ./run-tests.ps1"
```

Expected: `Passed!`, everything green.

- [ ] **Step 13: Commit**

```bash
git add src/VvCash/ViewModels/PosViewModel.cs src/VvCash/Views/PosView.axaml src/VvCash/Views/CustomerDisplayWindow.axaml
git commit -m "feat(units): quantity pad and unit display on the register"
```

---

## Before opening the PR

- [ ] Full suite green: `powershell -NoProfile -Command "& ./run-tests.ps1"`
- [ ] A real sale of a unit product reaches the server and is accepted. If the server answers `unit_id ... does not match the product's unit`, the sync is not carrying `unit_id` — check the backend change in `cashes/cash_repo.go` is deployed to that tenant.
- [ ] The unrelated working-tree changes in `CartItem.cs`, `CartService.cs` and `CartServiceQuoteTest.cs` are still uncommitted, or were committed separately on purpose.

## Known gap, by design

`sell_in_secondary_unit` does not exist on the backend yet — it needs a migration, the product API, the cash sync payload, and a field in the back office (`bozor` repository). Until it lands, `Product.SellInSecondaryUnit` parses as `false` for every product, so the pad opens in pieces and the cashier flips the toggle by hand on unit goods. Everything else works. Task 3 already reads the key, so the register picks it up the moment the server starts sending it — no register change needed.
