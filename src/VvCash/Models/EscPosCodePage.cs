using System;
using System.Collections.Generic;
using System.Text;

namespace VvCash.Models;

/// <summary>Одна кодовая страница термопринтера: чем кодировать байты и каким
/// номером сказать принтеру, как их читать.
///
/// Обе половины нужны и по разным причинам. <see cref="CodePage"/> определяет,
/// какие байты уходят; <see cref="EscTSelector"/> — как принтер их истолкует.
/// Разойдутся — получится другой мусор вместо нынешнего.</summary>
public sealed class EscPosCodePage
{
    private Encoding? _encoding;

    // Регистрация живёт на типе, который зовёт GetEncoding, а не на каталоге.
    // Явный статический конструктор снимает beforefieldinit, поэтому CLR выполнит
    // его до появления первого экземпляра — включая инициализаторы полей самого
    // каталога, которые эти экземпляры и создают. Иначе запись, построенную мимо
    // каталога, ждал бы NotSupportedException, а каталог с неленивым Encoding —
    // TypeInitializationException при первом же обращении.
    static EscPosCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EscPosCodePage(string id, string displayName, int codePage, byte escTSelector)
    {
        Id = id;
        DisplayName = displayName;
        CodePage = codePage;
        EscTSelector = escTSelector;
    }

    /// <summary>То, что ложится в настройки. Хранится он, а не DisplayName:
    /// правка подписи в интерфейсе не должна ломать настроенную кассу.</summary>
    public string Id { get; }

    /// <summary>Не переводится и живёт в коде — как DisplayName у PhoneFormat.
    /// Номер таблицы опознаётся независимо от письменности.</summary>
    public string DisplayName { get; }

    public int CodePage { get; }

    /// <summary>n в команде ESC t n.</summary>
    public byte EscTSelector { get; }

    /// <summary>Замена непокрытой буквы на «?» названа здесь явно, а не оставлена
    /// на умолчание. Таджикских ӯ ғ қ ҳ ҷ и казахских ә ң ө ұ ү і нет ни в одной
    /// однобайтовой таблице ESC/POS, то есть подстановка будет случаться на живых
    /// названиях товаров. Падать нельзя — чек обязан выйти; прятать нечестно —
    /// поэтому она предъявляется в пробной печати.
    ///
    /// Ширина подстановки тоже не свободна для правки. PadLine и Truncate меряют
    /// строку в символах ещё до кодирования, и это совпадает с шириной в байтах на
    /// бумаге только потому, что таблица однобайтовая, а «?» — ровно один символ:
    /// один непокрытый символ входа даёт ровно один байт выхода, как и любой
    /// покрытый. Раздуй подстановку до «??» — подсчёт останется прежним, а байтов
    /// на выходе станет больше, и колонка с ценой на чеке молча съедет.
    /// ExceptionFallback вместо неё возвращает ту самую ошибку, которую первый
    /// абзац запрещает: чек обязан выйти, а не упасть на первом непокрытом
    /// символе.</summary>
    public Encoding Encoding => _encoding ??= Encoding.GetEncoding(
        CodePage,
        new EncoderReplacementFallback("?"),
        new DecoderReplacementFallback("?"));
}

/// <summary>Каталог. Не редактируется из интерфейса сознательно: кассир не должен
/// иметь возможности задать таблицу, которой у принтера нет. Новая запись — правка
/// этого файла и релиз, ровно как с PhoneFormats.
///
/// Значения EscTSelector — из нумерации таблиц Epson, которой следует большинство
/// клонов. У CP866 селектор 17 поддержан почти повсеместно; у CP1251 в природе
/// встречаются 6, 7 и 46, и угадать нужный из репозитория нельзя — это вторая
/// причина, по которой выбор вынесен в настройку с пробной печатью.</summary>
public static class EscPosCodePages
{
    public static readonly EscPosCodePage Cp866 =
        new("CP866", "CP866 — кириллица (DOS)", 866, 17);

    public static readonly EscPosCodePage Cp1251 =
        new("CP1251", "CP1251 — кириллица (Windows)", 1251, 46);

    public static readonly EscPosCodePage Pc437 =
        new("PC437", "PC437 — латиница (таблица по умолчанию)", 437, 0);

    public static IReadOnlyList<EscPosCodePage> All { get; } =
        Array.AsReadOnly(new[] { Cp866, Cp1251, Pc437 });

    /// <summary>Чем становится принтер, у которого настройку не трогали. CP866 —
    /// её понимает большинство ESC/POS-клонов на этом рынке.</summary>
    public static EscPosCodePage Default => Cp866;

    /// <summary>Единственное место, где Id превращается в запись. Функцией, а не
    /// веткой по месту: правило «пусто или незнакомо — значит CP866» должно быть
    /// одно и проверяться тестом.</summary>
    public static EscPosCodePage Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var page in All)
            {
                if (string.Equals(page.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return page;
                }
            }
        }

        return Default;
    }
}
