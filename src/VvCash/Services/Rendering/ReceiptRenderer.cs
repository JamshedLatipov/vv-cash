using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using VvCash.Models;
using VvCash.Models.Receipt;

namespace VvCash.Services.Rendering;

/// <summary>Шаблон плюс данные продажи → плоский список операций. Чистая
/// функция: ни байтов, ни кодовой страницы, ни сокетов. Всё, что знает про
/// раскладку, живёт здесь и только здесь.</summary>
public static class ReceiptRenderer
{
    private static readonly Regex Placeholder = new(@"\{([a-zA-Z]+)\}", RegexOptions.Compiled);

    public static IReadOnlyList<ReceiptOp> Render(ReceiptTemplate template, SaleReceiptData sale)
    {
        var ops = new List<ReceiptOp>();
        var values = Values(sale);

        foreach (var block in template.Blocks)
        {
            if (!block.Enabled) continue;
            RenderBlock(block, template, sale, values, ops);
        }

        // Ничего не напечатано (все блоки выключены, включая пустой шаблон
        // без блоков вовсе) — резать нечего: CutOp тут был бы обрезкой пустой
        // ленты, а не завершением документа.
        if (ops.Count > 0) ops.Add(new CutOp());
        return ops;
    }

    private static void RenderBlock(ReceiptBlock block, ReceiptTemplate template, SaleReceiptData sale,
        IReadOnlyDictionary<string, string> values, List<ReceiptOp> ops)
    {
        // ПОРЯДОК ОПЕРАЦИЙ ВОКРУГ БЛОКА — НЕ ВКУСОВЩИНА, А УСЛОВИЕ БАЙТ-В-БАЙТ.
        //
        // Каждый блок обрамляется одинаково: пролог Align → DoubleSize → Bold,
        // тело, эпилог Bold(false) → DoubleSize(false). Три свойства этой схемы
        // проверены на всех пяти фикстурах замка, и каждое обязательно.
        //
        // 1. Атрибуты СБРАСЫВАЮТСЯ после блока, а не только выставляются перед.
        //    Нынешний код выключает двойной размер сразу после шапки, и без
        //    эпилога этот ESC ! уезжает вниз по чеку — на бумаге весь корпус
        //    пошёл бы двойной шириной, а каждая строка переносилась бы.
        //
        // 2. Сбрасывают ВСЕ блоки, включая нетекстовые. Иначе разделитель,
        //    позиции и итоги не объявляют шрифтовых атрибутов вовсе, и жирный,
        //    включённый на номере бегунка, дотягивается до конца чека.
        //
        // 3. В прологе DoubleSize идёт СНАРУЖИ Bold, в эпилоге наоборот. Это
        //    прямое следствие того, что ESC ! гасит emphasized, а эмиттер после
        //    него переиздаёт ESC E (см. EscPosEmitter, ветка DoubleSizeOp).
        //    Обратный порядок даёт на чеке с бегунком 418 байт против 424:
        //    пара «выключить и тут же включить двойной размер» схлопывается,
        //    и замок краснеет.
        //
        // Лишних байтов эта схема не рождает: эмиттер следит за состоянием и
        // молчит, когда команда уже в силе.

        // Проверка пустой подстановки — ДО пролога. Блок, который решено не
        // печатать, не должен оставить за собой висячий AlignOp: смена
        // выравнивания без текста выдаёт ESC a в никуда и сдвигает байты.
        string? line = null;
        if (block is TextBlock tb && !TrySubstitute(tb.Content, values, out line)) return;

        ops.Add(new AlignOp(block.Align));

        switch (block)
        {
            case TextBlock t:
                ops.Add(new DoubleSizeOp(t.DoubleSize));
                ops.Add(new BoldOp(t.Bold));
                ops.Add(new TextOp(line!));
                break;

            case LineBlock l:
                var count = l.Count > 0 ? l.Count : template.Width;
                var ch = string.IsNullOrEmpty(l.Char) ? "-" : l.Char.Substring(0, 1);
                ops.Add(new DoubleSizeOp(false));
                ops.Add(new BoldOp(false));
                ops.Add(new TextOp(string.Concat(System.Linq.Enumerable.Repeat(ch, count))));
                break;

            case FeedBlock f:
                ops.Add(new DoubleSizeOp(false));
                ops.Add(new BoldOp(false));
                ops.Add(new FeedOp(f.Lines));
                break;

            case FieldsBlock fields:
                ops.Add(new DoubleSizeOp(false));
                ops.Add(new BoldOp(false));
                foreach (var field in fields.Fields)
                {
                    if (!values.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value))
                        continue;
                    ops.Add(new TextOp(field.Label + value));
                }
                break;

            case ItemsBlock items:
                ops.Add(new DoubleSizeOp(false));
                ops.Add(new BoldOp(false));
                foreach (var item in sale.Items) RenderItem(item, items, template.Width, ops);
                break;

            case TotalsBlock totals:
                RenderTotals(totals, sale, template.Width, ops);
                break;

            default:
                // QR, штрихкод и логотип подключаются в Task 8. До тех пор блок
                // просто не печатается — это лучше, чем падение на чеке.
                break;
        }

        ops.Add(new BoldOp(false));
        ops.Add(new DoubleSizeOp(false));
    }

    private static void RenderItem(CartItem item, ItemsBlock cfg, int width, List<ReceiptOp> ops)
    {
        ops.Add(new TextOp(ReceiptText.PadLine(
            $"{item.Product.Name} x{item.QuantityDisplay}",
            ReceiptText.Money(item.LineTotal),
            width)));

        if (cfg.ShowUnitPrice)
            ops.Add(new TextOp($"    {item.QuantityDisplay} x {ReceiptText.Money(item.Product.Price)}"));

        if (cfg.ShowSku && !string.IsNullOrWhiteSpace(item.Product.Sku))
            ops.Add(new TextOp($"    {item.Product.Sku}"));

        if (cfg.ShowBarcode && !string.IsNullOrWhiteSpace(item.Product.Barcode))
            ops.Add(new TextOp($"    {item.Product.Barcode}"));

        if (cfg.ShowSecondaryUnit && item.Product.HasSecondaryUnit)
            ops.Add(new TextOp($"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}"));
    }

    private static void RenderTotals(TotalsBlock cfg, SaleReceiptData sale, int width, List<ReceiptOp> ops)
    {
        // AlignOp здесь нет: его добавляет общий пролог RenderBlock, одинаковый
        // для всех блоков. Дубль был бы безвреден (эмиттер его подавит), но
        // рассогласовал бы этот метод с остальными.
        ops.Add(new DoubleSizeOp(false));
        ops.Add(new BoldOp(false));

        if (cfg.ShowSubtotal)
            ops.Add(new TextOp(ReceiptText.PadLine(cfg.SubtotalLabel, ReceiptText.Money(sale.Subtotal), width)));

        if (cfg.ShowDiscount && sale.Discount > 0)
        {
            ops.Add(new TextOp(ReceiptText.PadLine(cfg.DiscountLabel, $"-{ReceiptText.Money(sale.Discount)}", width)));
            if (cfg.ShowDiscountName && !string.IsNullOrWhiteSpace(sale.DiscountName))
                ops.Add(new TextOp(ReceiptText.Truncate(sale.DiscountName!, width)));
        }

        ops.Add(new BoldOp(cfg.BoldTotal));
        ops.Add(new TextOp(ReceiptText.PadLine(cfg.TotalLabel, ReceiptText.Money(sale.Total), width)));
        ops.Add(new BoldOp(false));
    }

    private static Dictionary<string, string> Values(SaleReceiptData sale) => new(StringComparer.Ordinal)
    {
        ["doc"] = sale.DocumentNumber ?? string.Empty,
        ["date"] = sale.SaleDate ?? string.Empty,
        ["warehouse"] = sale.WarehouseName ?? string.Empty,
        ["seller"] = sale.SellerName ?? string.Empty,
        ["queue"] = sale.QueueNumber ?? string.Empty,
        ["subtotal"] = ReceiptText.Money(sale.Subtotal),
        ["discount"] = ReceiptText.Money(sale.Discount),
        ["total"] = ReceiptText.Money(sale.Total),
        ["discountName"] = sale.DiscountName ?? string.Empty,
    };

    /// <summary>Подставляет значения. Возвращает false, если хоть одна известная
    /// подстановка пуста — тогда строка не печатается вовсе. Это то же, что
    /// делают четыре if в нынешнем BuildSaleReceipt: у офлайновой продажи нет
    /// номера, и пустая строка вместо него — мусор, а не информация.
    ///
    /// Незнакомое имя не считается пустым и остаётся на бумаге как есть: {tota}
    /// сразу показывает, где опечатка в бэкофисе.</summary>
    private static bool TrySubstitute(string content, IReadOnlyDictionary<string, string> values, out string result)
    {
        var dropped = false;

        result = Placeholder.Replace(content, m =>
        {
            var key = m.Groups[1].Value;
            if (!values.TryGetValue(key, out var value)) return m.Value;
            if (string.IsNullOrWhiteSpace(value)) dropped = true;
            return value;
        });

        return !dropped;
    }
}
