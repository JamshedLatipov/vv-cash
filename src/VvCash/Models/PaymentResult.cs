namespace VvCash.Models;

public class PaymentResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
