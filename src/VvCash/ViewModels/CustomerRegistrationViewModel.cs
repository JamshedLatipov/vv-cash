using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using VvCash.Models.Api;
using VvCash.Models;
using VvCash.Services;
using VvCash.Services.Api;

namespace VvCash.ViewModels;

public partial class CustomerRegistrationViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly ICounterpartyService _counterpartyService;

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

    public string FormattedPhoneNumber
    {
        get
        {
            if (string.IsNullOrEmpty(PhoneNumber)) return "+7 (___) ___-__-__";

            // Basic formatting for Russia +7 (900) 000-00-00
            // Assuming max 10 digits after +7
            string formatted = "+7";
            var digits = PhoneNumber;

            if (digits.Length > 0)
            {
                var area = digits.Length > 3 ? digits.Substring(0, 3) : digits;
                formatted += $" ({area}";
                if (digits.Length >= 3) formatted += ") ";

                var part1 = digits.Length > 6 ? digits.Substring(3, 3) : (digits.Length > 3 ? digits.Substring(3) : "");
                formatted += part1;

                if (digits.Length >= 6) formatted += "-";

                var part2 = digits.Length > 8 ? digits.Substring(6, 2) : (digits.Length > 6 ? digits.Substring(6) : "");
                formatted += part2;

                if (digits.Length >= 8) formatted += "-";

                var part3 = digits.Length > 10 ? digits.Substring(8, 2) : (digits.Length > 8 ? digits.Substring(8) : "");
                formatted += part3;
            }

            return formatted;
        }
    }

    public CustomerRegistrationViewModel(Window window, ICounterpartyService counterpartyService)
    {
        _window = window;
        _counterpartyService = counterpartyService;
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
        OnPropertyChanged(nameof(FormattedPhoneNumber));
    }

    [RelayCommand]
    private void Numpad(string digit)
    {
        if (PhoneNumber.Length < 10)
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

        var request = new CounterpartyCreateRequest
        {
            FirstName = string.IsNullOrWhiteSpace(FirstName) ? "-" : FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(LastName) ? "-" : LastName.Trim(),
            Gender = SelectedGenderIndex == 0 ? "MALE" : "FEMALE",
            Email = string.IsNullOrWhiteSpace(Email) ? null : Email,
            Phone = PhoneNumber.Length == 10 ? $"7{PhoneNumber}" : null, // Assuming format requires Country Code
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

        _window.Close(response);
    }

    [RelayCommand]
    private void Cancel()
    {
        _window.Close(null); // Return cancelled
    }
}
