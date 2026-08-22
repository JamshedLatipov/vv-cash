using System;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services.Hardware;

/// <summary>Дисплей покупателя такой, каким его задали настройки — и пересобранный,
/// как только их поменяли.
///
/// По образцу CompositePrinterService, а не «прочитать настройки один раз при
/// старте»: иначе после настройки порта пришлось бы перезапускать кассу.
///
/// Не «Composite»: дисплей на кассе один, складывать нечего. Общее с принтерным
/// композитом — только подписка на SettingsChanged и подмена внутренностей одним
/// движением.</summary>
public class ConfiguredCustomerDisplayService : ICustomerDisplayService
{
    private readonly ISettingsService _settingsService;

    /// <summary>volatile по той же причине, что _printers у композита: присваивание
    /// ссылки атомарно, но атомарность — не видимость.
    ///
    /// Замок вокруг Rebuild, который есть у композита, здесь не нужен: там он
    /// защищает шаг отписки от прежнего состава, а у дисплея отписываться не от
    /// чего — Rebuild просто заменяет одну ссылку.</summary>
    private volatile ICustomerDisplayService _inner = new NullCustomerDisplayService();

    /// <summary>Что сейчас собрано из настроек. Только для теста, по образцу
    /// CompositePrinterService.Printers: иначе строка, которая доносит порт,
    /// скорость и кодовую страницу до железа, не покрыта ничем.</summary>
    internal ICustomerDisplayService Inner => _inner;

    public ConfiguredCustomerDisplayService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var port = _settingsService.CustomerDisplayPort;

        _inner = string.IsNullOrWhiteSpace(port)
            ? new NullCustomerDisplayService()
            : new VfdDisplayService(
                port,
                _settingsService.CustomerDisplayBaudRate,
                EscPosCodePages.Resolve(_settingsService.CustomerDisplayCodePageId));
    }

    public Task<bool> ShowLineAsync(string line1, string line2) => _inner.ShowLineAsync(line1, line2);
    public Task<bool> ShowItemAsync(string name, decimal price) => _inner.ShowItemAsync(name, price);
    public Task<bool> ShowTotalAsync(decimal total) => _inner.ShowTotalAsync(total);
    public Task<bool> ClearAsync() => _inner.ClearAsync();
}
