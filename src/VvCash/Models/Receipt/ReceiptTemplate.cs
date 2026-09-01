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

    /// <summary>internal, а не private: генератор превью шаблона в этом же
    /// репозитории обязан сериализовать блоки теми же настройками, которыми их
    /// потом читает касса. Две раздельные копии одних и тех же
    /// JsonSerializerOptions — тот самый дубль, который разъедется в первый же
    /// день, когда кто-то добавит WriteIndented на одной стороне и забудет про
    /// другую.</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Разбирает значение опции receipt_template. Любая беда — пустое
    /// значение, не-JSON, чужая версия, дубль ключа — читается как «шаблона
    /// нет», и касса печатает дефолт. Бросать нельзя: значение приходит с
    /// сервера и из кэша, а чек обязан выйти.
    ///
    /// Catch без списка типов исключений — намеренно. Раньше здесь стоял
    /// фильтр (JsonException/FormatException/InvalidOperationException), и на
    /// дубле ключа наружу вылетал необработанный ArgumentException: дубли
    /// ключей — законный JSON по RFC 8259, а опция шесть лет правилась вручную
    /// через текстовое поле в бэкофисе, так что дубль — не гипотеза. JsonObject
    /// материализует свой внутренний словарь лениво и бросает на первом же
    /// обращении по индексатору, то есть на node["version"]. Перечисление
    /// конкретных типов исключений — это заявка на то, что все пути известны
    /// заранее, а этот случай доказал обратное: единственная операция, которая
    /// не имеет права бросить из Parse, — это сам Parse.
    ///
    /// Отсутствие ключа "blocks" — тоже такая беда, а не осознанно пустой
    /// шаблон: receiptTemplate живёт в конфиге с 2019 года и шесть лет
    /// рендерился обычным текстовым полем в бэкофисе, и случайный валидный
    /// JSON-объект без нужных ключей там вполне мог осесть. То же верно для
    /// "blocks":null — сервер на Go сериализует так nil-слайс (json.Marshal
    /// для nil-слайса даёт literal null, а не "[]"). А вот "blocks":[] —
    /// наоборот, осознанный выбор администратора, стёршего все блоки в
    /// конструкторе шаблонов, и его нельзя подменять дефолтом: разница ровно в
    /// том, что лежит по ключу "blocks", и is-паттерн ниже проверяет это одной
    /// веткой на все три формы мусора (нет ключа, null, не массив) сразу.
    ///
    /// Незнакомый type блока выбрасывается, а остальные печатаются: касса и
    /// админка обновляются врозь, и блок из более новой админки не повод
    /// потерять весь чек. То же — для элемента, который вообще не объект
    /// (null, строка, число), и для объекта, у которого type — не строка
    /// (TryGetValue вместо GetValue не даёт этому бросить). Дальше каждый
    /// блок разбирается отдельно, в своём try/catch: битое поле внутри одного
    /// блока (число там, где ждали строку, или наоборот) роняет только этот
    /// блок, а не документ целиком — раньше один общий Deserialize на весь
    /// шаблон терял вместе с плохим блоком и соседние, исправные.</summary>
    public static ReceiptTemplate Parse(string? raw)
    {
        raw = raw?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(raw)) return Default;

        try
        {
            var node = JsonNode.Parse(raw!)?.AsObject();
            if (node == null) return Default;

            var version = node["version"]?.GetValue<int>() ?? CurrentVersion;
            if (version != CurrentVersion)
            {
                Console.WriteLine($"[ReceiptTemplate] версия {version} не поддерживается (ожидалась {CurrentVersion}), печатаю дефолт");
                return Default;
            }

            // node["blocks"] возвращает null и для отсутствующего ключа, и для
            // JSON null; is-паттерн ниже отбрасывает разом отсутствие ключа,
            // null и любое не-массивное значение — одной явной веткой, а не
            // выброшенным исключением.
            if (node["blocks"] is not JsonArray blocksArray)
            {
                Console.WriteLine("[ReceiptTemplate] blocks отсутствует, null или не список, печатаю дефолт");
                return Default;
            }

            var width = node["width"]?.GetValue<int>() ?? 32;

            var blocks = new List<ReceiptBlock>();
            foreach (var blockNode in blocksArray)
            {
                if (blockNode is not JsonObject obj) continue;

                try
                {
                    // TryGetValue, а не GetValue: {"type":1} не должен бросать —
                    // число тут тоже чужой блок, просто без опознаваемого type,
                    // а не повод откатить весь документ.
                    if (obj["type"] is not JsonValue typeValue
                        || !typeValue.TryGetValue<string>(out var type)
                        || !KnownTypes.Contains(type))
                    {
                        continue;
                    }

                    var block = obj.Deserialize<ReceiptBlock>(Options);
                    if (block != null) blocks.Add(block);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ReceiptTemplate] блок в blocks не разобран: {ex.GetType().Name}: {ex.Message}, пропускаю");
                }
            }

            return new ReceiptTemplate { Version = version, Width = width, Blocks = blocks };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ReceiptTemplate] значение не разобрано: {ex.GetType().Name}: {ex.Message}, печатаю дефолт");
            return Default;
        }
    }

    /// <summary>internal, по той же причине, что и Options: тест на рефлексии
    /// сверяет этот список с атрибутами [JsonDerivedType] на ReceiptBlock,
    /// чтобы десятый тип, добавленный только атрибутом и без правки этого
    /// списка, не начал молча выбрасываться из каждого шаблона — симптом,
    /// неотличимый от штатной работы.</summary>
    internal static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "text", "line", "feed", "fields", "items", "totals", "qr", "barcode", "logo",
    };

    /// <summary>Ровно нынешняя раскладка, переписанная блок в блок с
    /// EscPosPrinterService.BuildSaleReceipt. Разделители — 28 дефисов
    /// (LineBlock.Count = 28 задан явно во всех трёх местах ниже), не по
    /// ширине ленты, потому что столько дефисов печатает сегодняшний чек, а
    /// замок совместимости считает байты; классовый дефолт LineBlock.Count —
    /// 0 («во всю ширину») и здесь ни при чём.
    ///
    /// Свойство-фабрика, а не поле с одним экземпляром на процесс: Parse
    /// отдаёт этот объект по ссылке на каждом аварийном пути, а Blocks —
    /// изменяемый список блоков с открытыми сеттерами. Один процессный
    /// синглтон, случайно испорченный вызывающим кодом, жил бы до перезапуска
    /// кассы и выглядел бы призраком: на точке «печатает не то», а в
    /// настройках и на сервере всё верно. Список к тому же не потокобезопасен
    /// — правка в одном потоке и печать в другом дали бы исключение посреди
    /// чека. new() на каждое обращение — тот же приём, что у
    /// CashFeatures.Default.</summary>
    public static ReceiptTemplate Default => new()
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
            new LineBlock { Align = ReceiptAlign.Center, Count = 28 },
            new ItemsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left, Count = 28 },
            new TotalsBlock { Align = ReceiptAlign.Left },
            new LineBlock { Align = ReceiptAlign.Left, Count = 28 },
            new TextBlock { Content = "Thank you for shopping!", Align = ReceiptAlign.Center },
            new FeedBlock { Lines = 2, Align = ReceiptAlign.Center },
        },
    };
}
