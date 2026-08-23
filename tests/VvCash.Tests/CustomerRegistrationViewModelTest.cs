using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;
using VvCash.Models.Api;
using VvCash.Services;
using VvCash.Services.Api;
using VvCash.ViewModels;
using Xunit;

namespace VvCash.Tests;

/// <summary>Поле телефона в окне регистрации: маска, предел нумпада, отказ на
/// неполном номере и то, что уезжает на сервер. View model развязана от Window
/// на делегат именно ради этих тестов — Avalonia здесь не поднимается.
///
/// Ради этого набора задача и делалась: молчаливый Phone = null дожил до
/// продакшена ровно потому, что проверять его было нечем.</summary>
public class CustomerRegistrationViewModelTest
{
    private sealed class FakeCounterpartyService : ICounterpartyService
    {
        public int CreateCount;
        public CounterpartyCreateRequest? LastRequest;

        /// <summary>Что вернёт создание. null — провал: именно так сервис
        /// сообщает об отказе сервера или отсутствии связи.</summary>
        public CounterpartyResponse? CreateResult = new() { Id = "c-1", FullNameRaw = "Новый Клиент" };

        public Task<CounterpartyResponse?> CreateCounterpartyAsync(CounterpartyCreateRequest request)
        {
            CreateCount++;
            LastRequest = request;
            return Task.FromResult(CreateResult);
        }

        public Task<List<CounterpartyResponse>?> SearchCounterpartiesAsync(string query)
            => Task.FromResult<List<CounterpartyResponse>?>(null);

        public Task<string?> GetSystemCounterpartyIdAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public string BackendUrl { get; set; } = string.Empty;
        public string CashRegisterToken { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public DateTime? AuthTokenExpiresAt { get; set; }
        public int SyncIntervalMinutes { get; set; } = 10;
        public string Language { get; set; } = "ru";
        public List<PrinterConfig> Printers { get; set; } = new();
        public bool ReturnOpenCashDrawer { get; set; }
        public bool ReturnPrintReceipt { get; set; }
        public string ExchangePayoutCategoryId { get; set; } = string.Empty;
        public string ReturnPayoutCategoryId { get; set; } = string.Empty;
        public string PhoneFormatId { get; set; } = string.Empty;
        public string CustomerDisplayPort { get; set; } = string.Empty;
        public int CustomerDisplayBaudRate { get; set; } = 9600;
        public string CustomerDisplayCodePageId { get; set; } = string.Empty;
        public event EventHandler? SettingsChanged;
        public void Save() { }
    }

    private sealed class Harness
    {
        public FakeCounterpartyService Service { get; } = new();
        public FakeSettingsService Settings { get; } = new();
        public int CloseCount;
        public CounterpartyResponse? ClosedWith;

        /// <param name="phoneFormatId">Пусто — формат по умолчанию, десять цифр.
        /// "TJ" — девятизначная касса, ради которой всё и затевалось.</param>
        public CustomerRegistrationViewModel Build(string phoneFormatId = "")
        {
            Settings.PhoneFormatId = phoneFormatId;
            return new CustomerRegistrationViewModel(
                result => { CloseCount++; ClosedWith = result; },
                Service,
                Settings);
        }
    }

    private static void Type(CustomerRegistrationViewModel vm, string digits)
    {
        foreach (var d in digits)
        {
            vm.NumpadCommand.Execute(d.ToString());
        }
    }

    [Fact]
    public void NineDigitFormat_ShowsItsOwnPlaceholder()
    {
        var vm = new Harness().Build("TJ");

        Assert.Equal("+992 (__) ___-__-__", vm.FormattedPhoneNumber);
    }

    /// <summary>Парная к предыдущей: без неё тест на таджикскую маску прошёл бы
    /// и на захардкоженном формате, если бы тот случайно совпал.</summary>
    [Fact]
    public void DefaultFormat_ShowsTheRussianPlaceholder()
    {
        var vm = new Harness().Build();

        Assert.Equal("+7 (___) ___-__-__", vm.FormattedPhoneNumber);
    }

    /// <summary>То самое место, где касса залипала: нумпад ждал десятую цифру,
    /// которой в таджикском номере не бывает.</summary>
    [Fact]
    public void Numpad_StopsAtTheFormatsDigitCount()
    {
        var vm = new Harness().Build("TJ");

        Type(vm, "901234567");
        Assert.Equal("901234567", vm.PhoneNumber);

        vm.NumpadCommand.Execute("8");

        Assert.Equal("901234567", vm.PhoneNumber);
        Assert.Equal("+992 (90) 123-45-67", vm.FormattedPhoneNumber);
    }

    [Fact]
    public void Backspace_RemovesOneDigit()
    {
        var vm = new Harness().Build("TJ");
        Type(vm, "901234567");

        vm.BackspaceCommand.Execute(null);

        Assert.Equal("90123456", vm.PhoneNumber);
    }

    /// <summary>Ради чего задача и делалась: раньше восемь цифр из девяти молча
    /// уезжали как Phone = null, и кассир получал клиента без телефона, ничего
    /// об этом не узнав.</summary>
    [Fact]
    public async Task HalfTypedNumber_IsRefusedAndNeverSent()
    {
        var harness = new Harness();
        var vm = harness.Build("TJ");
        Type(vm, "90123456");   // восемь из девяти

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, harness.Service.CreateCount);
        Assert.Equal(0, harness.CloseCount);
    }

    /// <summary>Сообщение обязано гаснуть, как только перестало быть правдой:
    /// иначе кассир дописывает девятую цифру, видит над полным номером «введён
    /// не полностью», читает это как «форма застряла» и жмёт ОТМЕНА — теряя ровно
    /// те данные, ради которых отказ и вводился.</summary>
    [Fact]
    public async Task EditingTheNumberAfterRefusal_ClearsTheMessage()
    {
        var harness = new Harness();
        var vm = harness.Build("TJ");
        Type(vm, "90123456");
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);

        vm.NumpadCommand.Execute("7");   // девятая цифра — номер полон

        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task FullNumber_IsSentWithTheFormatsCountryCode()
    {
        var harness = new Harness();
        var vm = harness.Build("TJ");
        Type(vm, "901234567");

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("992901234567", harness.Service.LastRequest?.Phone);
        Assert.Equal(1, harness.CloseCount);
        Assert.Same(harness.Service.CreateResult, harness.ClosedWith);
    }

    /// <summary>Другой формат — другой код страны и другая длина. Без этой пары
    /// тест выше прошёл бы и на захардкоженной девятке.</summary>
    [Fact]
    public async Task DefaultFormat_SendsTenDigitsWithSeven()
    {
        var harness = new Harness();
        var vm = harness.Build();
        Type(vm, "9001234567");

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("79001234567", harness.Service.LastRequest?.Phone);
    }

    /// <summary>Телефон — единственное обязательное поле карточки клиента: без
    /// него сохранять больше нельзя, даже если остальные поля заполнены.</summary>
    [Fact]
    public async Task EmptyPhone_IsRefusedAndNeverSent()
    {
        var harness = new Harness();
        var vm = harness.Build("TJ");
        vm.FirstName = "Иван";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, harness.Service.CreateCount);
        Assert.Equal(0, harness.CloseCount);
    }

    /// <summary>Провал создания оставляет окно открытым с заполненной формой:
    /// закрытие с null означало бы «отмена», а отменял не кассир.</summary>
    [Fact]
    public async Task FailedCreate_KeepsTheWindowOpen()
    {
        var harness = new Harness();
        harness.Service.CreateResult = null;
        var vm = harness.Build("TJ");
        Type(vm, "901234567");

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, harness.CloseCount);
        Assert.Equal("901234567", vm.PhoneNumber);
    }

    [Fact]
    public void ApplyPrefill_WritesPhoneAndNames()
    {
        var vm = new Harness().Build("TJ");

        vm.ApplyPrefill(new CustomerPrefill
        {
            PhoneNumber = "901234567",
            FirstName = "Иван",
            LastName = "Петров",
        });

        Assert.Equal("901234567", vm.PhoneNumber);
        Assert.Equal("Иван", vm.FirstName);
        Assert.Equal("Петров", vm.LastName);
        Assert.Equal("+992 (90) 123-45-67", vm.FormattedPhoneNumber);
    }

    /// <summary>Строка «Иванов» даёт только имя, и обнулять из-за неё уже
    /// набранный телефон незачем.</summary>
    [Fact]
    public void ApplyPrefill_DoesNotBlankWhatThePrefillLeavesEmpty()
    {
        var vm = new Harness().Build("TJ");
        Type(vm, "901234567");

        vm.ApplyPrefill(new CustomerPrefill { FirstName = "Иванов" });

        Assert.Equal("901234567", vm.PhoneNumber);
        Assert.Equal("Иванов", vm.FirstName);
    }

    [Fact]
    public void Cancel_ClosesWithNull()
    {
        var harness = new Harness();
        var vm = harness.Build();

        vm.CancelCommand.Execute(null);

        Assert.Equal(1, harness.CloseCount);
        Assert.Null(harness.ClosedWith);
    }
}
