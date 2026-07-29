using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using VvCash.Models;

namespace VvCash.ViewModels;

/// <summary>Computes the money side of an exchange: what the returned goods are
/// worth against what the cashier is handing out instead. Two baskets —
/// <see cref="ReturnedLines"/> reuses ReturnLineVm, the same object
/// ReturnsViewModel builds from GET /documents/return/{id}/; <see cref="IssuedLines"/>
/// reuses CartItem, the same Product+Quantity pairing the register already prices
/// a cart with — so both totals round the exact way a sale or a return already
/// does, through the store's MoneyPolicy.</summary>
public partial class ExchangeViewModel : ViewModelBase
{
    private readonly MoneyPolicy _moneyPolicy;

    [ObservableProperty] private ObservableCollection<ReturnLineVm> _returnedLines = new();
    [ObservableProperty] private ObservableCollection<CartItem> _issuedLines = new();

    /// <summary>Exchanges are online-only (see ExchangeService remarks): with no
    /// connection there is nowhere to queue the request, so the submit button
    /// must not offer it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isOnline;

    /// <summary>From ReturnDetailBody.ExchangeAllowed — false once the receipt is
    /// past the store's exchange window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _exchangeAllowed = true;

    public ExchangeViewModel(MoneyPolicy? moneyPolicy = null)
    {
        _moneyPolicy = moneyPolicy ?? MoneyPolicy.Default;
    }

    /// <summary>Rounded the way the server books it — the figure the cashier
    /// reads on screen must match what the receipt ends up saying.</summary>
    public decimal ReturnedTotal => _moneyPolicy.Round(ReturnedLines.Sum(l => l.LineRefund));
    public decimal IssuedTotal => _moneyPolicy.Round(IssuedLines.Sum(l => l.LineTotal));
    public decimal Difference => IssuedTotal - ReturnedTotal;

    /// <summary>True once the replacement costs more than what came back.</summary>
    public bool CustomerPays => Difference > 0;

    /// <summary>True once the replacement costs less — the till owes the customer.</summary>
    public bool TillPays => Difference < 0;

    /// <summary>Absolute amount the till hands back when <see cref="TillPays"/> —
    /// shown without a minus sign, since the label already carries the direction.</summary>
    public decimal RefundDue => TillPays ? -Difference : 0m;

    public bool CanSubmit => IsOnline && ExchangeAllowed
        && ReturnedLines.Any(l => l.ReturnQty > 0)
        && IssuedLines.Any(l => l.Quantity > 0);

    /// <summary>Replaces the returned-goods basket (e.g. after loading a receipt)
    /// and rewires per-line notifications so a quantity edit on any line updates
    /// the totals on screen.</summary>
    public void SetReturnedLines(IEnumerable<ReturnLineVm> lines)
    {
        foreach (var l in ReturnedLines) l.RefundChanged -= OnBasketChanged;
        ReturnedLines = new ObservableCollection<ReturnLineVm>(lines);
        foreach (var l in ReturnedLines) l.RefundChanged += OnBasketChanged;
        RaiseTotalsChanged();
    }

    /// <summary>Adds one line to the issued-goods basket (a product the cashier
    /// picked to replace the returned item) and wires its quantity changes into
    /// the totals.</summary>
    public void AddIssuedLine(CartItem item)
    {
        item.PropertyChanged += OnIssuedLinePropertyChanged;
        IssuedLines.Add(item);
        RaiseTotalsChanged();
    }

    public void RemoveIssuedLine(CartItem item)
    {
        item.PropertyChanged -= OnIssuedLinePropertyChanged;
        IssuedLines.Remove(item);
        RaiseTotalsChanged();
    }

    private void OnIssuedLinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CartItem.Quantity))
            RaiseTotalsChanged();
    }

    private void OnBasketChanged() => RaiseTotalsChanged();

    private void RaiseTotalsChanged()
    {
        OnPropertyChanged(nameof(ReturnedTotal));
        OnPropertyChanged(nameof(IssuedTotal));
        OnPropertyChanged(nameof(Difference));
        OnPropertyChanged(nameof(CustomerPays));
        OnPropertyChanged(nameof(TillPays));
        OnPropertyChanged(nameof(RefundDue));
        OnPropertyChanged(nameof(CanSubmit));
    }
}
