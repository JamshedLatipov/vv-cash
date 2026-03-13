using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public interface IDiscountService
{
    Task<Coupon?> ValidateCouponAsync(string code);
}
