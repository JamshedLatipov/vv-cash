using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VvCash.Models.Api;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;

namespace VvCash.ViewModels;

public partial class CustomerRegistrationViewModel : ViewModelBase
{
    private readonly ICounterpartyService _counterpartyService;

    /// <summary>Закрыть окно, отдав созданного клиента либо null. Делегат, а не
    /// Window: владеть окном должна вызывающая сторона — иначе view model нельзя
    /// сконструировать в тесте, а тест с window == null проверял бы ветку,
    /// которой в проде нет. Тот же приём в CustomerSearchViewModel.</summary>
    private readonly Action<CounterpartyResponse?> _close;

    /// <summary>Снимок формата на момент открытия окна — как PosViewModel
    /// снимает фича-флаги. Формат не может измениться, пока окно открыто:
    /// меняют его на экране настроек, а он модальный.</summary>
    private readonly PhoneFormat _phoneFormat;

    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private int _selectedGenderIndex = 0; // 0 = MALE, 1 = FEMALE

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private DateTime? _dateOfBirth;
    [ObservableProperty] private bool _isLoyaltyEnrolled = true;
    [ObservableProperty] private string _phoneNumber = string.Empty;

    /// <summary>Провал создания клиента. Окно при этом не закрывается: раньше
    /// оно закрывалось с null и на отмене, и на отказе сервера, так что кассир,
    /// нажавший «Сохранить», получал просто исчезнувшее окно и никакого
    /// объяснения — а введённые данные терялись вместе с ним.</summary>
    [ObservableProperty] private string? _errorMessage;

    public string FormattedPhoneNumber => _phoneFormat.Format(PhoneNumber);

    /// <summary>Сколько цифр ждёт это окно. Наружу — чтобы строку поиска
    /// разбирал тот же формат, которым живёт форма: разойдись они, окно открылось
    /// бы с номером, который нумпад не может дописать, а «Сохранить» отвергает.</summary>
    public int PhoneDigitCount => _phoneFormat.DigitCount;

    public CustomerRegistrationViewModel(
        Action<CounterpartyResponse?> close,
        ICounterpartyService counterpartyService,
        ISettingsService settingsService)
    {
        _close = close;
        _counterpartyService = counterpartyService;
        _phoneFormat = PhoneFormats.Resolve(settingsService.PhoneFormatId);
    }

    /// <summary>Переносит строку поиска в форму, когда регистрацию открыли из
    /// окна поиска. Пустые поля префилла ничего не пишут: строка «Иванов» даёт
    /// только имя, и обнулять из-за неё телефон незачем.</summary>
    public void ApplyPrefill(CustomerPrefill prefill)
    {
        if (!string.IsNullOrEmpty(prefill.PhoneNumber)) PhoneNumber = prefill.PhoneNumber;
        if (!string.IsNullOrEmpty(prefill.FirstName)) FirstName = prefill.FirstName;
        if (!string.IsNullOrEmpty(prefill.LastName)) LastName = prefill.LastName;
    }

    partial void OnPhoneNumberChanged(string value)
    {
        // Правка номера снимает и сообщение о нём: иначе кассир, дописавший
        // девятую цифру, продолжает видеть «введён не полностью» над номером,
        // который уже полон, и читает это как «форма застряла».
        ErrorMessage = null;
        OnPropertyChanged(nameof(FormattedPhoneNumber));
    }

    [RelayCommand]
    private void Numpad(string digit)
    {
        if (PhoneNumber.Length < _phoneFormat.DigitCount)
        {
            PhoneNumber += digit;
        }
    }

    [RelayCommand]
    private void Backspace()
    {
        if (PhoneNumber.Length > 0)
        {
            PhoneNumber = PhoneNumber.Substring(0, PhoneNumber.Length - 1);
        }
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = null;

        // Пустой телефон законен — клиент без телефона нормальная запись. А вот
        // начатый и не дописанный раньше молча превращался в Phone = null:
        // кассир набирал восемь цифр из девяти, жал «Сохранить» и получал
        // клиента без телефона, ничего об этом не узнав.
        if (PhoneNumber.Length > 0 && PhoneNumber.Length != _phoneFormat.DigitCount)
        {
            ErrorMessage = I18nService.Instance["PhoneIncomplete"];
            return;
        }

        var request = new CounterpartyCreateRequest
        {
            FirstName = string.IsNullOrWhiteSpace(FirstName) ? "-" : FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(LastName) ? "-" : LastName.Trim(),
            Gender = SelectedGenderIndex == 0 ? "MALE" : "FEMALE",
            Email = string.IsNullOrWhiteSpace(Email) ? null : Email,
            Phone = PhoneNumber.Length == _phoneFormat.DigitCount ? _phoneFormat.CountryCode + PhoneNumber : null,
            Birthday = DateOfBirth?.ToString("yyyy-MM-dd'T'00:00:00Z"), // Parse into valid string
            Form = "individual" // Default based on requirement
        };

        CounterpartyResponse? response;
        try
        {
            response = await _counterpartyService.CreateCounterpartyAsync(request);
        }
        catch (Exception)
        {
            // ICounterpartyService не обещает, что реализация не бросает, а
            // необработанное исключение из команды роняет кассу.
            response = null;
        }

        if (response == null)
        {
            // Окно остаётся открытым с заполненной формой: кассир видит причину
            // и может повторить, не набирая всё заново. Закрытие с null здесь
            // означало бы «отмена», а отменял не он.
            ErrorMessage = I18nService.Instance["CustomerCreateFailed"];
            return;
        }

        _close(response);
    }

    [RelayCommand]
    private void Cancel()
    {
        _close(null); // Return cancelled
    }
}
