using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace VvCash.Tests;

/// <summary>Читает словари локализации прямо с диска.
///
/// Мимо I18nService намеренно: он берёт их через AssetLoader.Open("avares://…"), а в
/// тестовом хосте Avalonia не поднята — LoadLanguage тихо уходит в catch, словарь
/// остаётся пустым, и на любой ключ возвращается заглушка "[ключ]". Из-за этого ни один
/// тест на view model не может отличить «ключа нет в словаре» от «ключ есть»: обе
/// стороны сравнения получают одну и ту же заглушку. Файлы с диска — единственное место,
/// где это вообще проверяемо.</summary>
public class I18nLocaleTest
{
    private static readonly string[] Locales = { "ru", "en", "kk", "tg", "uz" };

    private static string LocaleDirectory()
    {
        // Тесты запускаются из build/verify-tests (см. run-tests.ps1), и глубина этого
        // пути — не то, на что стоит закладываться константой. Поднимаемся до корня по
        // файлу решения, как единственной приметe, которая не переедет вместе с выходной
        // папкой.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "vv-cash.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "VvCash", "Assets", "i18n");
    }

    private static Dictionary<string, string> Load(string locale)
    {
        var path = Path.Combine(LocaleDirectory(), $"{locale}.json");
        Assert.True(File.Exists(path), $"словарь не найден: {path}");

        // ReadAllText сам снимает BOM, которым эти файлы записаны; JsonSerializer на нём
        // споткнулся бы.
        var json = File.ReadAllText(path);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        Assert.NotNull(map);
        return map!;
    }

    [Fact]
    public void DisplayCheckKeys_ExistInEveryLocale()
    {
        // Три исхода кнопки «Проверить дисплей» — три разных сообщения, и все три обязаны
        // быть переведены везде. Непереведённое показывается кассиру как "[ключ]": не
        // падение, а сообщение, которое ничего не сообщает, — ровно то, от чего эта
        // кнопка и лечится.
        //
        // Проверяются именно эти три ключа, а не полное совпадение словарей: ru сегодня
        // расходится с остальными четырьмя на два десятка ключей, и тест «все словари
        // одинаковы» был бы красным с рождения по причинам, к дисплею не относящимся.
        string[] keys = { "DisplayCheckOk", "DisplayCheckFailed", "DisplayCheckNoPort" };

        foreach (var locale in Locales)
        {
            var map = Load(locale);
            foreach (var key in keys)
            {
                Assert.True(map.ContainsKey(key), $"{locale}.json: нет ключа {key}");
                Assert.False(string.IsNullOrWhiteSpace(map[key]), $"{locale}.json: {key} пуст");
            }
        }
    }

    [Fact]
    public void PaymentProcessedReceiptNotPrinted_ExistsInEveryLocale()
    {
        // PosViewModel reads this key when the sale posted but PrintReceiptAsync came
        // back false — the one branch that used to tell the cashier "Thank you!" over a
        // receipt that never printed. An untranslated locale would show "[key]" instead,
        // which at least is not a false "all good", but a real translation is the point.
        foreach (var locale in Locales)
        {
            var map = Load(locale);
            Assert.True(map.ContainsKey("PaymentProcessedReceiptNotPrinted"),
                $"{locale}.json: нет ключа PaymentProcessedReceiptNotPrinted");
            Assert.False(string.IsNullOrWhiteSpace(map["PaymentProcessedReceiptNotPrinted"]),
                $"{locale}.json: PaymentProcessedReceiptNotPrinted пуст");
        }
    }

    [Fact]
    public void DisplayCheckNoPort_IsNotTheSameStringAsDisplayCheckFailed()
    {
        // Два исхода, которые чинятся в разных местах: незаполненное поле — в соседней
        // выпадашке, отказ проверки — кабелем, драйвером и питанием табло. Совпади тексты
        // — и разделение веток в CheckDisplay перестанет что-либо значить для того, кто
        // стоит у кассы, хотя код останется правильным и все прочие тесты зелёными.
        foreach (var locale in Locales)
        {
            var map = Load(locale);
            Assert.NotEqual(map["DisplayCheckFailed"], map["DisplayCheckNoPort"]);
        }
    }
}
