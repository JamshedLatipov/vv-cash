using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;

namespace VvCash.ViewModels;

public partial class CustomerRegistrationViewModel : ViewModelBase
{
    private readonly Window _window;

    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _dateOfBirth = string.Empty;
    [ObservableProperty] private bool _isLoyaltyEnrolled = true;
    [ObservableProperty] private string _phoneNumber = string.Empty;

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

    public CustomerRegistrationViewModel(Window window)
    {
        _window = window;
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
    private void Submit()
    {
        // Add business logic for saving customer here if needed
        _window.Close(true); // Return success
    }

    [RelayCommand]
    private void Cancel()
    {
        _window.Close(false); // Return cancelled
    }
}
