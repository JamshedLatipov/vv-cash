using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VvCash.Models;

public partial class Product : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;

    /// <summary>Category <b>id</b>, not its display name — the cash sync sends the
    /// id under the "category" key. Promotion targeting matches against it.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Tag ids, for tag-targeted promotions evaluated offline.</summary>
    public List<string> TagIds { get; set; } = new();
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;

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

    /// <summary>Stock for this register's warehouse as of the last complete
    /// reconciliation walk, or null when no walk has completed yet.
    ///
    /// Null and zero are different answers and must not be collapsed: null is "nobody
    /// has checked", zero is "checked, and there is none to sell". Only the second one puts a
    /// badge on the tile.
    ///
    /// Ignored on the one path a Product is ever serialised: the parked-sale snapshot
    /// (ParkedSaleService.ParkAsync/ResumeAsync). A parked sale can sit for hours, and
    /// PosViewModel resumes it by handing the deserialised Product straight back into the
    /// live cart with no fresh catalog lookup (see PosViewModel.ResumeParkedSale) —
    /// so a number written here would ride back in looking current when it is only as
    /// current as the moment the sale was parked. Left out, a resumed item reads as null,
    /// which reads as "not reconciled" — the honest answer for a figure nothing here can
    /// still vouch for.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal? StockQuantity { get; set; }

    /// <summary>Whether the register knows this product to be out of stock. Deliberately
    /// false for null — a register that has never reconciled must behave exactly as it
    /// did before reconciliation existed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOutOfStock => StockQuantity <= 0m;

    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private Bitmap? _imageBitmap;
}
