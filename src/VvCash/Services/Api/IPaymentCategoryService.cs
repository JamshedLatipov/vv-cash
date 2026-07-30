using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models.Api;

namespace VvCash.Services.Api;

public interface IPaymentCategoryService
{
    /// <summary>Lists the store's payment categories so the settings screen can offer
    /// them. Returns an empty list rather than throwing when the server cannot be
    /// reached — the settings screen is reachable offline and must still open.</summary>
    Task<List<PaymentCategory>> GetPaymentCategoriesAsync();
}
