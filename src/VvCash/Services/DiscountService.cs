using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public class DiscountService : IDiscountService
{
    private readonly List<Coupon> _coupons = new()
    {
        new Coupon { Code = "SAVE10", DiscountPercent = 10, Description = "10% off" },
        new Coupon { Code = "WELCOME5", DiscountPercent = 5, Description = "5% off" },
        new Coupon { Code = "FLAT20", DiscountAmount = 20, Description = "$20 off" },
    };

    public Task<Coupon?> ValidateCouponAsync(string code)
    {
        var coupon = _coupons.FirstOrDefault(c => c.Code == code.ToUpperInvariant());
        return Task.FromResult(coupon);
    }
}
