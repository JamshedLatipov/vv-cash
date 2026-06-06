using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

public interface IParkedSaleService
{
    /// <summary>Сохранить снимок корзины как отложенный чек. total — итоговая сумма к оплате на момент отложения.</summary>
    Task<ParkedSale> ParkAsync(ParkedSaleSnapshot snapshot, decimal total);

    Task<IReadOnlyList<ParkedSale>> GetAllAsync();

    /// <summary>Загрузить снимок и удалить запись (чек «вынут» в активную корзину). null — если не найдено.</summary>
    Task<ParkedSaleSnapshot?> ResumeAsync(string id);

    Task DeleteAsync(string id);
    Task<int> GetCountAsync();

    event EventHandler<int>? CountChanged;
}
