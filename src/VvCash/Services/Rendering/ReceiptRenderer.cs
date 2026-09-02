using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    public static IReadOnlyList<ReceiptOp> Render(ReceiptTemplate template, SaleReceiptData sale,
        string? logoJson = null)
    {
        var ops = new List<ReceiptOp>();
        var values = Values(sale);

        foreach (var block in template.Blocks)
        {
            if (!block.Enabled) continue;
            RenderBlock(block, template, sale, values, logoJson, ops);
        }

        // Резать нечего, если на бумагу не вышло ни одного знака — ни
        // текстового, ни графического. Считать по длине ops (как было раньше)
        // неверно: AlignOp/BoldOp/DoubleSizeOp сами по себе ничего не
        // печатают, а их достаточно, чтобы список не был пуст.
        //
        // QrOp/BarcodeOp/NvLogoOp/BitmapOp входят в условие наравне с
        // TextOp/FeedOp: они кладут краску на бумагу точно так же, а не
        // "меньше считаются". Раньше условие смотрело только на TextOp/FeedOp
        // — это было верно ровно до тех пор, пока QR/штрихкод/логотип не
        // печатали вовсе ничего (см. историю этого файла); теперь чек из
        // одного QR без единой текстовой строки без графики в этом условии
        // выходил бы без обрезки и склеивался со следующим чеком на ленте.
        if (ops.Any(o => o is TextOp or FeedOp or QrOp or BarcodeOp or NvLogoOp or BitmapOp))
            ops.Add(new CutOp());
        return ops;
    }

    private static void RenderBlock(ReceiptBlock block, ReceiptTemplate template, SaleReceiptData sale,
        IReadOnlyDictionary<string, string> values, string? logoJson, List<ReceiptOp> ops)
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
        // qr и barcode подставляют данные тем же TrySubstitute, что и текст, —
        // значит и проверка для них стоит здесь же, а не внутри switch: там она
        // была бы уже ПОСЛЕ AlignOp двумя строками ниже и не спасла бы от той же
        // самой висячей команды, от которой спасает эта проверка для текста.
        //
        // Пустая строка — ДВЕ разные беды, а не одна. TrySubstitute ловит
        // только подстановку, схлопнувшуюся в пустоту ({doc} у продажи без
        // номера). Литеральная "" — значение по умолчанию у QrBlock.Data и
        // BarcodeBlock.Data, когда блок добавили в конструкторе и не
        // заполнили — вообще не проходит через подстановку (в строке без
        // "{...}" Placeholder.Replace не находит совпадений), и TrySubstitute
        // вернула бы true как для любой другой строки без плейсхолдеров.
        // Поэтому итоговая строка ниже проверяется на пустоту ЯВНО, отдельно
        // от dropped.
        string? line = null;
        if (block is TextBlock tb && !TrySubstitute(tb.Content, values, out line)) return;

        string? qrData = null;
        if (block is QrBlock qrPre)
        {
            if (!TrySubstitute(qrPre.Data, values, out qrData) || string.IsNullOrWhiteSpace(qrData))
                return;
        }

        string? bcData = null;
        if (block is BarcodeBlock bcPre)
        {
            if (!TrySubstitute(bcPre.Data, values, out bcData) || string.IsNullOrWhiteSpace(bcData))
                return;

            // Длина, алфавит и формат — уже здесь, а не только в эмиттере:
            // печатать испорченный штрихкод (обрезанный по однобайтовой
            // длине, прочитанный как обычный текст из-за отсутствующего
            // селектора CODE128, отвергнутый принтером как EAN-13 вне
            // диапазона) хуже, чем не напечатать вовсе.
            if (!EscPosEmitter.TryEncodeBarcode(bcData, bcPre.Symbology, out _, out var reason))
            {
                Console.WriteLine($"[ReceiptRenderer] штрихкод не напечатан: {reason}");
                return;
            }
        }

        // Логотип целиком — какую операцию он даст, если вообще какую-то —
        // решается ДО пролога, тем же приёмом, что qr и barcode чуть выше:
        // опция receipt_logo из бэкофиса могла ещё не доехать (наполовину
        // настроенная касса) или прийти битой, и в обоих случаях блок не
        // должен оставить за собой висячий AlignOp — как и qr/barcode с
        // пустыми данными несколькими строками выше.
        //
        // Switch-ВЫРАЖЕНИЕ с явным arm на каждое известное значение LogoSource
        // и явным `_ => null` на любое другое — а не проверка "если не Nv,
        // значит Bitmap" (которая однажды уже была здесь и в switch на блок
        // ниже, и превращала любое третье значение LogoSource в
        // NullReferenceException: охранник, проверявший только Source ==
        // Bitmap, не срабатывал, bitmapOp оставался null, но switch ниже всё
        // равно клал его в ops как "не Nv, значит бери bitmapOp"). Разбор
        // шаблона принимает число вне диапазона enum без проверки, а опция
        // источника логотипа шесть лет правилась руками через текстовое поле
        // — третье значение достижимо не только гипотетически.
        ReceiptOp? logoOp = null;
        if (block is LogoBlock logoPre)
        {
            logoOp = logoPre.Source switch
            {
                LogoSource.Nv => new NvLogoOp(logoPre.NvSlot),
                LogoSource.Bitmap => ParseLogo(logoJson),
                _ => null,
            };
            if (logoOp == null) return;
        }

        ops.Add(new AlignOp(block.Align));

        switch (block)
        {
            case TextBlock t:
                ops.Add(new DoubleSizeOp(t.DoubleSize));
                ops.Add(new BoldOp(t.Bold));
                AddText(ops, line!);
                break;

            case LineBlock l:
                var count = l.Count > 0 ? l.Count : template.Width;
                ops.Add(new DoubleSizeOp(false));
                ops.Add(new BoldOp(false));
                // l.Char гарантированно ровно один непустой символ — сеттер
                // LineBlock.Char сам это обеспечивает (см. Blocks.cs), так что
                // проверять IsNullOrEmpty здесь незачем.
                AddText(ops, new string(l.Char[0], count));
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
                    // Пустой ключ — это "поле ещё не выбрали в конструкторе",
                    // а не опечатка: показывать "{}" покупателю незачем.
                    // Проверка ДО ветки "неизвестный ключ", иначе пустой Key
                    // (гарантированно не найдётся в values — там пустых
                    // ключей нет) попал бы в неё и напечатал бы Label + "{}".
                    if (string.IsNullOrEmpty(field.Key)) continue;

                    // Незнакомый ключ и известный-но-пустой — разные беды и не
                    // должны молча схлопываться в одно и то же "пропустить".
                    // Опечатка в ключе (field.Key вроде "sellr") обязана быть
                    // видна на бумаге ровно так же, как опечатка в подстановке
                    // TextBlock ({tota}) — иначе на один и тот же класс ошибки
                    // в одном и том же конструкторе шаблонов заведены две
                    // противоположные политики.
                    if (!values.TryGetValue(field.Key, out var value))
                    {
                        AddText(ops, field.Label + "{" + field.Key + "}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value)) continue;
                    AddText(ops, field.Label + value);
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

            case QrBlock qr:
                // qrData уже проверен и подставлен выше, до AlignOp; здесь он
                // гарантированно не null и не пуст, потому что иначе метод
                // вернулся раньше.
                ops.Add(new QrOp(qrData!, qr.ModuleSize));
                break;

            case BarcodeBlock bc:
                // bcData уже проверен, подставлен и провалидирован
                // (TryEncodeBarcode) выше, до AlignOp.
                ops.Add(new BarcodeOp(bcData!, bc.Symbology, bc.Height, bc.PrintHri));
                break;

            case LogoBlock:
                // logoOp уже вычислен целиком (NvLogoOp, BitmapOp — или null,
                // и тогда метод вернулся раньше) в switch-выражении до
                // AlignOp. Единственный источник истины о том, что печатает
                // LogoBlock, — там; здесь его незачем пересчитывать заново
                // отдельной проверкой Source, которая рискует разойтись с
                // первой (см. историю этого файла).
                ops.Add(logoOp!);
                break;

            default:
                // switch по типу блока не проверяется компилятором на
                // полноту: все девять подтипов ReceiptBlock перечислены выше,
                // и эта ветка недостижима, пока их ровно девять. Тихий
                // no-op здесь замаскировал бы забытый новый тип блока под
                // "решили не печатать" — тем же способом, каким рассогласован
                // был бы EscPosEmitter.Emit, если бы её default молчал вместо
                // NotSupportedException (см. EscPosEmitterTest).
                throw new NotSupportedException($"Неизвестный тип блока чека: {block.GetType().Name}");
        }

        ops.Add(new BoldOp(false));
        ops.Add(new DoubleSizeOp(false));
    }

    private static void RenderItem(CartItem item, ItemsBlock cfg, int width, List<ReceiptOp> ops)
    {
        AddText(ops, ReceiptText.PadLine(
            $"{item.Product.Name} x{item.QuantityDisplay}",
            ReceiptText.Money(item.LineTotal),
            width));

        // UnitPrice, не Product.Price: сервер оценивает корзину по каталогу
        // склада и игнорирует цену, присланную кассой, поэтому
        // QuotedUnitPrice — когда он есть — и есть то, что реально платит
        // клиент (см. CartItem.UnitPrice и CartService.ApplyQuotedPrices).
        // item.LineTotal чуть выше уже посчитан из UnitPrice; печатать здесь
        // Product.Price означало бы показать цену, с которой сумма строки не
        // сходится арифметически.
        if (cfg.ShowUnitPrice)
            AddText(ops, $"    {item.QuantityDisplay} x {ReceiptText.Money(item.UnitPrice)}");

        if (cfg.ShowSku && !string.IsNullOrWhiteSpace(item.Product.Sku))
            AddText(ops, $"    {item.Product.Sku}");

        if (cfg.ShowBarcode && !string.IsNullOrWhiteSpace(item.Product.Barcode))
            AddText(ops, $"    {item.Product.Barcode}");

        if (cfg.ShowSecondaryUnit && item.Product.HasSecondaryUnit)
            AddText(ops, $"    {item.QuantityInUnitDisplay} {item.Product.UnitShortName}");

        // Label настраиваемый (ItemsBlock.LineDiscountLabel), а не зашитая
        // латиница: этот же чек несёт TotalsBlock.DiscountLabel из настроек
        // администратора, и два разных слова для одного смысла на одной
        // бумаге были бы хуже одного непереведённого.
        if (cfg.ShowLineDiscount && item.HasLineDiscount)
            AddText(ops, ReceiptText.PadLine("    " + cfg.LineDiscountLabel, $"-{ReceiptText.Money(item.LineDiscount)}", width));
    }

    private static void RenderTotals(TotalsBlock cfg, SaleReceiptData sale, int width, List<ReceiptOp> ops)
    {
        // AlignOp здесь нет: его добавляет общий пролог RenderBlock, одинаковый
        // для всех блоков. Дубль был бы безвреден (эмиттер его подавит), но
        // рассогласовал бы этот метод с остальными.
        ops.Add(new DoubleSizeOp(false));
        ops.Add(new BoldOp(false));

        if (cfg.ShowSubtotal)
            AddText(ops, ReceiptText.PadLine(cfg.SubtotalLabel, ReceiptText.Money(sale.Subtotal), width));

        if (cfg.ShowDiscount && sale.Discount > 0)
        {
            AddText(ops, ReceiptText.PadLine(cfg.DiscountLabel, $"-{ReceiptText.Money(sale.Discount)}", width));
            if (cfg.ShowDiscountName && !string.IsNullOrWhiteSpace(sale.DiscountName))
                AddText(ops, ReceiptText.Truncate(sale.DiscountName!, width));
        }

        ops.Add(new BoldOp(cfg.BoldTotal));
        AddText(ops, ReceiptText.PadLine(cfg.TotalLabel, ReceiptText.Money(sale.Total), width));
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
    /// сразу показывает, где опечатка в бэкофисе.
    ///
    /// Правило не умеет различать "пусто" и "ноль": {discount} всегда
    /// раскрывается в "0.00" (Money никогда не возвращает пустую строку), а
    /// не в string.Empty, так что подстановка никогда не считается пустой из-
    /// за нулевой скидки. Значит текстовый блок вроде "Скидка: {discount}"
    /// печатается на КАЖДОЙ продаже, включая те, где скидки нет — в отличие
    /// от TotalsBlock, который в этом случае строку скидки прячет
    /// (cfg.ShowDiscount && sale.Discount > 0). Два способа показать скидку в
    /// одном шаблоне ведут себя по-разному, и это сознательно не исправлено
    /// здесь: TrySubstitute работает с любым именем одинаково и не обязан
    /// знать, что "discount" — особое, денежное. TS-двойник обязан повторить
    /// именно это, а не "исправленную" версию.</summary>
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

    /// <summary>Единственная точка, строящая TextOp. TextOp своей
    /// документацией запрещает "\n" в своей строке: она пройдёт эмиттер
    /// насквозь и превратится на бумаге в две строки мимо переноса по ширине.
    /// TextBlock.Content чистит перевод строки в своём сеттере, но это не
    /// единственный источник свободного текста на чеке — имя товара, SKU,
    /// штрихкод, название акции, Label поля и ЛЮБОЕ подставленное значение
    /// приходят из того же конструктора шаблонов и того же бэкофиса, а
    /// подстановка в TrySubstitute как раз обходит санитайзер TextBlock.Content
    /// стороной. Один узкий проход здесь держит инвариант в одном месте
    /// вместо семи мест, которые иначе пришлось бы поддерживать заново на
    /// каждый новый источник текста.</summary>
    private static void AddText(List<ReceiptOp> ops, string line) => ops.Add(new TextOp(Sanitize(line)));

    /// <summary>Снимает ВСЕ управляющие символы, не только перевод строки.
    /// Таб и ESC проходили бы иначе сырыми: PadLine считает их печатными
    /// колонками (строка "шириной 32" выходит короче на бумаге), а сырой ESC
    /// в потоке — это первый байт чужой команды принтера (например ESC C —
    /// "задать длину страницы"), которая съест следующий байт как свой
    /// параметр. LineBlock.Char в этой же фиче уже снимает все char.IsControl
    /// этим же доводом; было бы две политики на один класс беды в одном файле.
    ///
    /// "\r\n" схлопывается в ОДИН пробел, а не в два отдельных: так же
    /// нормализует его TextBlock.Content, и этот метод обязан совпасть с ним
    /// на строке, которая раньше шла только через тот сеттер. Замена идёт до
    /// общего фильтра по char.IsControl нарочно — если сначала снимать
    /// одиночные управляющие, "\r" и "\n" превратятся в два независимых
    /// пробела до того, как заменится их пара. TS-двойник обязан повторить
    /// именно порядок "сначала \r\n целиком, потом остальные управляющие".</summary>
    private static string Sanitize(string s)
    {
        var normalized = s.Replace("\r\n", " ");
        return normalized.Any(char.IsControl)
            ? new string(normalized.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            : normalized;
    }

    /// <summary>Разбирает опцию receipt_logo: ширина в БАЙТАХ, высота в
    /// точках, растр в base64 (см. BitmapOp — тот же порядок полей и та же
    /// единица измерения ширины, потому что столько же требует GS v 0).
    ///
    /// Любая беда здесь — "логотипа нет", а не исключение, роняющее печать:
    /// блок включён, а картинка ещё не доехала синхронизацией (или доехала
    /// битой) — это наполовину настроенная касса, а не повод не напечатать
    /// чек целиком. Список перехватываемых типов собран по факту того, что
    /// умеет бросить каждый шаг разбора, а не расширен "на всякий случай":
    /// - JsonException — сам JSON не разобрался (не json вовсе, оборванная
    ///   строка);
    /// - InvalidOperationException — root не объект (GetProperty), или
    ///   widthBytes/height лежат не числом (GetInt32 требует
    ///   JsonValueKind.Number), или raster — не строка (GetString);
    /// - KeyNotFoundException — GetProperty не нашёл нужное поле;
    /// - FormatException — raster не валидный base64;
    /// - ArgumentException — бросает сам конструктор BitmapOp: размер вне
    ///   диапазона 1..65535 (ArgumentOutOfRangeException — это наследник
    ///   ArgumentException, один catch ловит оба), включая widthBytes==0 или
    ///   height==0 (см. BitmapOp.EnsureDimensionInRange: "логотип" нулевой
    ///   площади — не изображение, а команда без определённого спекой
    ///   смысла, и "логотип очищен в бэкофисе" — куда вероятнее прочтение
    ///   нулевого размера, чем "напечатай пустую картинку"), или объявленный
    ///   WidthBytes×Height не сходится с фактической длиной растра. Без этого
    ///   пункта испорченный (несходящийся по размеру или нулевой) receipt_logo
    ///   ронял бы печать целиком вместо того, чтобы остаться без логотипа, —
    ///   то самое требование задачи: конструктор BitmapOp бросает на
    ///   рассинхроне размера и длины, и разбор обязан это поймать.
    ///
    /// Нижняя граница размера сознательно не продублирована здесь отдельной
    /// проверкой ДО вызова BitmapOp (как было раньше, до ревью): единственная
    /// граница — в конструкторе, рядом с верхней, и любой будущий вызывающий
    /// BitmapOp получает обе бесплатно, а не только верхнюю. Эта функция
    /// просто ловит то, что он бросает, — тем же catch, что и рассинхрон
    /// размера и длины.
    ///
    /// Зовётся по разу на каждый LogoBlock{Source: Bitmap} в шаблоне, а не
    /// один раз на чек: обычный шаблон несёт не больше одного такого блока, и
    /// разницы нет, но шаблон с двумя разберёт один и тот же logoJson дважды.
    /// Не кэшировано намеренно — измеренная стоимость разбора (порядка
    /// микросекунд на реальном растре) на четыре порядка ниже бюджета печати
    /// одного документа, и кэш добавил бы состояние ради экономии, которой
    /// никто не заметит.</summary>
    private static BitmapOp? ParseLogo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;
            var widthBytes = root.GetProperty("widthBytes").GetInt32();
            var height = root.GetProperty("height").GetInt32();
            var raster = Convert.FromBase64String(root.GetProperty("raster").GetString() ?? "");

            return new BitmapOp(raster, widthBytes, height);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException)
        {
            Console.WriteLine($"[ReceiptRenderer] логотип не разобран, печатаю без него: {ex.Message}");
            return null;
        }
    }
}
