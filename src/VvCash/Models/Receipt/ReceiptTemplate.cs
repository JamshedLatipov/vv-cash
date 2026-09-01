using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VvCash.Models.Receipt;

public sealed class ReceiptTemplate
{
    /// <summary>Формат самого шаблона, не его содержимого. Чужая версия —
    /// повод не печатать по нему вовсе: половина незнакомого формата хуже
    /// знакомого дефолта.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Колонок ленты: 32 на 58 мм, 42–48 на 80 мм.
    ///
    /// Кламп в СЕТТЕРЕ, а не в Parse: разбор — не единственный вход. Объектный
    /// инициализатор (`new ReceiptTemplate { Width = ... }`) прошёл бы мимо
    /// проверки в Parse молча, а так пишут и тесты этого плана, и рано или поздно
    /// напишет боевой код.
    ///
    /// Проверка нужна потому, что непроверенная ширина ведёт себя в помощниках
    /// несогласованно: Truncate(s, 0) молча съедает название акции, а
    /// Truncate(s, -1) бросает. Сам ReceiptText при этом остаётся без ветвлений —
    /// он объявлен контрактом для TS-двойника, и каждая ветка внутри него это
    /// ветка, которую двойник обязан повторить.</summary>
    private int _width = 32;

    public int Width
    {
        get => _width;
        set => _width = value > 0 ? value : 32;
    }

    public List<ReceiptBlock> Blocks { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Разбирает значение опции receipt_template. Любая беда — пустое
    /// значение, не-JSON, чужая версия — читается как «шаблона нет», и касса
    /// печатает дефолт. Бросать нельзя: значение приходит с сервера и из кэша,
    /// а чек обязан выйти.
    ///
    /// Отсутствие ключа "blocks" — тоже такая беда, а не осознанно пустой
    /// шаблон: receiptTemplate живёт в конфиге с 2019 года и шесть лет
    /// рендерился обычным текстовым полем в бэкофисе, и случайный валидный
    /// JSON-объект без нужных ключей там вполне мог осесть. "blocks":[] —
    /// наоборот, осознанный выбор администратора, стёршего все блоки в
    /// конструкторе шаблонов, и его нельзя подменять дефолтом.
    ///
    /// Незнакомый type блока выбрасывается, а остальные печатаются: касса и
    /// админка обновляются врозь, и блок из более новой админки не повод
    /// потерять весь чек. То же — для элемента, который вообще не объект
    /// (голый null, строка, число): это такой же чужой блок, просто без type,
    /// а не повод откатить весь чек в дефолт.</summary>
    public static ReceiptTemplate Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Default;

        try
        {
            var node = JsonNode.Parse(raw!)?.AsObject();
            if (node == null) return Default;

            var version = node["version"]?.GetValue<int>() ?? CurrentVersion;
            if (version != CurrentVersion) return Default;

            // Ключа "blocks" нет вовсе — печатать нечего, и это не то же самое,
            // что "blocks":[] (см. комментарий к методу). Различие ровно в
            // присутствии ключа, поэтому здесь ContainsKey, а не проверка на
            // null/пустоту значения.
            if (!node.ContainsKey("blocks")) return Default;

            var kept = new JsonArray();
            foreach (var block in node["blocks"]?.AsArray() ?? new JsonArray())
            {
                // Элемент массива, который не является JSON-объектом (null,
                // строка, число), не имеет и не может иметь "type" — это такой
                // же чужой блок, как и объект с незнакомым type, и должен
                // выбрасываться поштучно. Раньше проверялась только null-ность
                // ("block?[...]"), а на скаляре сам индексатор JsonNode звал
                // AsObject() и бросал InvalidOperationException — это ловил
                // внешний catch и откатывал в Default весь шаблон целиком,
                // а не только сломанный элемент.
                if (block is not JsonObject obj) continue;

                var type = obj["type"]?.GetValue<string>();
                if (type != null && KnownTypes.Contains(type))
                    kept.Add(obj.DeepClone());
            }
            node["blocks"] = kept;

            // Ширина из JSON может быть нулём или отрицательной — её клампит сеттер
            // Width, через который проходит и десериализация. Отдельной проверки
            // здесь нет намеренно: она была бы вторым местом с той же политикой.
            return JsonSerializer.Deserialize<ReceiptTemplate>(node.ToJsonString(), Options) ?? Default;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            Console.WriteLine($"[ReceiptTemplate] значение не разобрано, печатаю дефолт: {ex.Message}");
            return Default;
        }
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "text", "line", "feed", "fields", "items", "totals", "qr", "barcode", "logo",
    };

    /// <summary>Ровно нынешняя раскладка, переписанная блок в блок с
    /// EscPosPrinterService.BuildSaleReceipt. Разделители — 28 дефисов, не по
    /// ширине ленты, потому что столько печатает сегодняшний чек.</summary>
    public static ReceiptTemplate Default { get; } = new()
    {
        Version = CurrentVersion,
        Width = 32,
        Blocks = new List<ReceiptBlock>
        {
            new TextBlock { Content = "VV CASH POS", Align = ReceiptAlign.Center, DoubleSize = true },
            new TextBlock { Content = "# {queue}", Align = ReceiptAlign.Center, Bold = true, DoubleSize = true },
            new FieldsBlock
            {
                Align = ReceiptAlign.Center,
                Fields = new List<ReceiptField>
                {
                    new() { Key = "doc", Label = "Doc #" },
                    new() { Key = "date", Label = "" },
                    new() { Key = "warehouse", Label = "Whse: " },
                    new() { Key = "seller", Label = "Seller: " },
                },
            },
            new LineBlock { Align = ReceiptAlign.Center },
            new ItemsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left },
            new TotalsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left },
            new TextBlock { Content = "Thank you for shopping!", Align = ReceiptAlign.Center },
            new FeedBlock { Lines = 2, Align = ReceiptAlign.Center },
        },
    };
}
