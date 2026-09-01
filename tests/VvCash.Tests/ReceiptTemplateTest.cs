using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using VvCash.Models.Receipt;
using Xunit;

namespace VvCash.Tests;

public class ReceiptTemplateTest
{
    // ReceiptTemplate.Default — это фабрика (new() на каждое обращение), а не
    // процессный синглтон, так что два вызова никогда не Assert.Same друг
    // друга. Сравниваем содержимое через ту же сериализацию, которой Parse
    // читает шаблон обратно: две структурно одинаковые модели дают
    // побайтово одинаковый JSON.
    private static string Json(ReceiptTemplate t) => JsonSerializer.Serialize(t, ReceiptTemplate.Options);

    [Fact]
    public void Parse_ReadsBlocksByTheirTypeDiscriminator()
    {
        var json = """
        {"version":1,"width":42,"blocks":[
          {"type":"text","content":"Магазин","align":"center","bold":true},
          {"type":"line","char":"=","count":10},
          {"type":"feed","lines":3}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(42, t.Width);
        var text = Assert.IsType<TextBlock>(t.Blocks[0]);
        Assert.Equal("Магазин", text.Content);
        Assert.Equal(ReceiptAlign.Center, text.Align);
        Assert.True(text.Bold);
        var line = Assert.IsType<LineBlock>(t.Blocks[1]);
        Assert.Equal("=", line.Char);
        Assert.Equal(10, line.Count);
        Assert.Equal(3, Assert.IsType<FeedBlock>(t.Blocks[2]).Lines);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnBrokenJson()
    {
        // В configs.val у существующих тенантов может лежать что угодно: опция
        // receiptTemplate засеяна в 2019 и шесть лет рендерилась текстовым полем.
        var t = ReceiptTemplate.Parse("не json вовсе");

        Assert.Equal(Json(ReceiptTemplate.Default), Json(t));
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnEmptyValue()
    {
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("")));
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse(null)));
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnADuplicateKey_InsteadOfThrowing()
    {
        // Дубли ключей — законный JSON по RFC 8259, а опция шесть лет
        // правилась вручную через текстовое поле в бэкофисе, так что дубль —
        // не гипотеза. JsonObject материализует внутренний словарь лениво и
        // бросает ArgumentException на первом же обращении по индексатору
        // (node["version"]). Раньше это вылетало из Parse наружу — касса не
        // печатала вообще ничего, что прямо противоречит обещанию метода.
        var json = """{"version":1,"version":2,"blocks":[{"type":"text","content":"A"}]}""";

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(Json(ReceiptTemplate.Default), Json(t));
    }

    [Fact]
    public void Parse_DropsAnUnknownBlockType_AndKeepsTheRest()
    {
        // Касса терпит блок из более новой админки, чем её собственная сборка.
        // Сервер такой type записать не даст, но обновляются они врозь.
        var json = """
        {"version":1,"width":32,"blocks":[
          {"type":"text","content":"A"},
          {"type":"hologram","spin":"fast"},
          {"type":"text","content":"B"}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(2, t.Blocks.Count);
        Assert.Equal(new[] { "A", "B" }, t.Blocks.Cast<TextBlock>().Select(b => b.Content));
    }

    [Fact]
    public void Parse_DropsANonObjectBlockEntry_AndKeepsTheRest()
    {
        // Симметрично уже проверенным null и объекту без "type": голая строка в
        // массиве blocks — тоже просто чужой блок без type, а не повод откатить
        // весь чек.
        var json = """{"version":1,"blocks":[{"type":"text","content":"A"},"oops",{"type":"text","content":"B"}]}""";

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(new[] { "A", "B" }, t.Blocks.Cast<TextBlock>().Select(b => b.Content));
    }

    [Fact]
    public void Parse_DropsABlockWithANonStringTypeValue_AndKeepsTheRest()
    {
        // {"type":1} — GetValue<string>() на числе бросает; TryGetValue не
        // бросает и возвращает false, так что этот блок теряет только себя, а
        // не роняет весь Deserialize вместе с исправным соседом "B".
        var json = """{"version":1,"blocks":[{"type":"text","content":"A"},{"type":1},{"type":"text","content":"B"}]}""";

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(new[] { "A", "B" }, t.Blocks.Cast<TextBlock>().Select(b => b.Content));
    }

    [Fact]
    public void Parse_DropsAStructurallyBrokenBlock_ButKeepsItsNeighbors()
    {
        // До поблочной десериализации один битый блок ронял единый
        // JsonSerializer.Deserialize на весь документ: админка новой версии,
        // поменявшая тип поля местами со строкой, стёрла бы чек целиком
        // вместе с исправными соседями "A" и "B". Теперь каждый блок
        // разбирается в своём try/catch, и падает только он.
        var json = """
        {"version":1,"blocks":[
          {"type":"text","content":"A"},
          {"type":"line","char":"=","count":"десять"},
          {"type":"text","content":"B"}
        ]}
        """;

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal(new[] { "A", "B" }, t.Blocks.Cast<TextBlock>().Select(b => b.Content));
    }

    [Fact]
    public void Parse_IgnoresAnUnknownFieldInsideAKnownBlock()
    {
        var t = ReceiptTemplate.Parse("""{"version":1,"blocks":[{"type":"text","content":"A","glitter":true}]}""");

        Assert.Equal("A", Assert.IsType<TextBlock>(t.Blocks[0]).Content);
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnAFutureVersion()
    {
        // Несовместимый формат лучше не печатать вовсе, чем печатать наполовину.
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("""{"version":99,"blocks":[]}""")));
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_OnAnObjectWithNoRecognizableKeys()
    {
        // receiptTemplate живёт в конфиге с 2019 года и шесть лет рендерился
        // обычным текстовым полем в бэкофисе — случайный валидный JSON-объект
        // без нужных ключей там вполне мог осесть. Без "blocks" это на бумаге
        // пустой чек: одна обрезка, без шапки, позиций и итогов. Формально
        // касса "напечатала", но выдала бессодержательный документ — это хуже,
        // чем напечатать дефолт.
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("{}")));
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("""{"a":1}""")));
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_WhenTheBlocksKeyIsMissing()
    {
        // Тот же случай, что и выше, но с валидной версией: версия сама по себе
        // не спасает документ без "blocks" — это всё ещё мусор, а не шаблон.
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("""{"version":1}""")));
    }

    [Fact]
    public void Parse_FallsBackToTheDefault_WhenBlocksIsNotAnArray()
    {
        // "blocks":null — не гипотеза: json.Marshal в Go сериализует так
        // nil-слайс, а сервер у нас на Go. Ключ присутствует, но значения нет,
        // и раньше "?? new JsonArray()" читал null как осознанно пустой список
        // — ровно тот пустой чек (одна обрезка), которого блокирует правило
        // для отсутствующего ключа. "nope" и {} — тоже не список, и обе формы
        // должны уйти в Default явной веткой, а не через выброшенное
        // исключение.
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("""{"version":1,"blocks":null}""")));
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("""{"version":1,"blocks":"nope"}""")));
        Assert.Equal(Json(ReceiptTemplate.Default), Json(ReceiptTemplate.Parse("""{"version":1,"blocks":{}}""")));
    }

    [Fact]
    public void Parse_KeepsAnIntentionallyEmptyBlockList()
    {
        // А вот "blocks":[] — это не порча данных, а осознанный выбор
        // администратора, стёршего все блоки в конструкторе шаблонов: ключ
        // присутствует и указывает на настоящий (пустой) список. Отличие от
        // предыдущих тестов ровно в том, что лежит по ключу "blocks", а не в
        // том, пуст итоговый список или нет.
        var t = ReceiptTemplate.Parse("""{"version":1,"blocks":[]}""");

        Assert.Empty(t.Blocks);
    }

    [Fact]
    public void Parse_StripsALeadingByteOrderMark()
    {
        // Конфиг, записанный виндовым инструментом, нередко получает BOM в
        // начале значения. Без TrimStart валидный JSON читался бы как мусор,
        // и Default подставлялся бы молча — обещание "не бросать" не
        // нарушено, но конфиг терялся незаметно для того, кто его настраивал.
        var json = "﻿" + """{"version":1,"blocks":[{"type":"text","content":"A"}]}""";

        var t = ReceiptTemplate.Parse(json);

        Assert.Equal("A", Assert.IsType<TextBlock>(t.Blocks[0]).Content);
    }

    [Fact]
    public void Parse_ClampsANonPositiveWidth_ToTheDefault()
    {
        // Ширина приезжает числом из JSON. Ноль заставил бы Truncate молча съесть
        // название акции, отрицательное — уронить печать целиком.
        Assert.Equal(32, ReceiptTemplate.Parse("""{"version":1,"width":0,"blocks":[]}""").Width);
        Assert.Equal(32, ReceiptTemplate.Parse("""{"version":1,"width":-5,"blocks":[]}""").Width);
    }

    [Fact]
    public void Width_ClampsANonPositiveValue_WhateverTheEntryPoint()
    {
        // Кламп живёт в сеттере именно затем, чтобы объектный инициализатор его не
        // обошёл: разбор JSON — не единственный вход, так пишут и тесты, и код.
        Assert.Equal(32, new ReceiptTemplate { Width = 0 }.Width);
        Assert.Equal(32, new ReceiptTemplate { Width = -5 }.Width);
        Assert.Equal(42, new ReceiptTemplate { Width = 42 }.Width);
    }

    [Fact]
    public void Parse_ClampsAnEmptyLineCharAndANegativeCount()
    {
        // Тот же довод, что у Width: разбор — не единственный вход, но и не
        // исключение из него. Пустой Char уронил бы рендер
        // IndexOutOfRangeException на первом символе разделителя,
        // отрицательный Count — ArgumentOutOfRangeException в конструкторе
        // повторяемой строки. Оба кламп получает через тот же сеттер, что и
        // объектный инициализатор.
        var t = ReceiptTemplate.Parse("""{"version":1,"blocks":[{"type":"line","char":"","count":-5}]}""");

        var line = Assert.IsType<LineBlock>(t.Blocks[0]);
        Assert.Equal("-", line.Char);
        Assert.Equal(0, line.Count);
    }

    [Fact]
    public void LineBlock_ClampsAnEmptyChar_ToTheDash()
    {
        Assert.Equal("-", new LineBlock { Char = "" }.Char);
    }

    [Fact]
    public void LineBlock_ClampsANegativeCount_ToZero()
    {
        Assert.Equal(0, new LineBlock { Count = -5 }.Count);
    }

    [Fact]
    public void LineBlock_ClampsAnExcessiveCount_ToTheCeiling()
    {
        // Гигантский Count (2e9 из мусорного конфига укладывается в int) ушёл
        // бы в повторяемую строку по числу символов — лента и память. Потолок
        // — 200, вдвое больше самой широкой поддерживаемой ленты.
        Assert.Equal(200, new LineBlock { Count = 2_000_000_000 }.Count);
    }

    [Fact]
    public void FeedBlock_ClampsANegativeLines_ToZero()
    {
        Assert.Equal(0, new FeedBlock { Lines = -5 }.Lines);
    }

    [Fact]
    public void Default_IsThirtyTwoColumnsWide()
    {
        Assert.Equal(32, ReceiptTemplate.Default.Width);
    }

    [Fact]
    public void Default_BlocksAreAllEnabled()
    {
        Assert.All(ReceiptTemplate.Default.Blocks, b => Assert.True(b.Enabled));
    }

    [Fact]
    public void Default_LineBlocksAllPrintTwentyEightDashes()
    {
        // Замок совместимости (SaleReceiptGoldenTest) считает байты: 28
        // дефисов, а не по ширине ленты. LineBlock.Count = 28 задан явно на
        // каждом из трёх LineBlock в Default — если это когда-нибудь снова
        // "потеряется" (как уже случалось: классовый дефолт был 28, а три
        // LineBlock в Default его не переопределяли и получали 28 по
        // умолчанию, из-за чего смена классового дефолта на 0 сломала бы
        // Default молча), этот тест покраснеет раньше, чем побайтовый замок.
        var lines = ReceiptTemplate.Default.Blocks.OfType<LineBlock>().ToList();

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(28, l.Count));
    }

    [Fact]
    public void KnownTypes_MatchesTheJsonDerivedTypeAttributes()
    {
        // KnownTypes и [JsonDerivedType] на ReceiptBlock — два независимых
        // списка одного и того же набора типов, ничем не связанных, кроме
        // дисциплины того, кто в следующий раз добавит десятый блок. Забытая
        // правка KnownTypes не бросает исключение и не падает в тестах на
        // новый тип — она просто молча выбрасывает такой блок из каждого
        // шаблона, и симптом неотличим от штатной работы. Этот тест ловит
        // расхождение сразу.
        var attributeDiscriminators = typeof(ReceiptBlock)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(a => (string)a.TypeDiscriminator!)
            .OrderBy(s => s)
            .ToList();

        Assert.Equal(attributeDiscriminators, ReceiptTemplate.KnownTypes.OrderBy(s => s).ToList());
    }
}
