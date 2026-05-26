using System.ComponentModel;
using System.Runtime.CompilerServices;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Models;

public sealed class ReinforcementTroopRuleItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _amountMode = "fixed";
    private int _amount = 1;

    public string AccountName { get; init; } = string.Empty;
    public string SourceVillageName { get; init; } = string.Empty;
    public string TroopType { get; init; } = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetValue(ref _isEnabled, value);
    }

    public string AmountMode
    {
        get => _amountMode;
        set
        {
            var normalized = NormalizeAmountMode(value);
            if (string.Equals(_amountMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _amountMode = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixedAmountMode));
            OnPropertyChanged(nameof(IsAllAvailableMode));
            OnPropertyChanged(nameof(IsPercentAvailableMode));
        }
    }

    public bool IsFixedAmountMode
    {
        get => string.Equals(AmountMode, "fixed", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                AmountMode = "fixed";
            }
        }
    }

    public bool IsAllAvailableMode
    {
        get => string.Equals(AmountMode, "all_available", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                AmountMode = "all_available";
            }
        }
    }

    public bool IsPercentAvailableMode => AmountMode.StartsWith("percent_", StringComparison.OrdinalIgnoreCase);

    public int Amount
    {
        get => _amount;
        set
        {
            var normalized = Math.Max(1, value);
            if (_amount == normalized)
            {
                return;
            }

            _amount = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AmountText));
        }
    }

    public string AmountText
    {
        get => Amount.ToString();
        set
        {
            if (int.TryParse(value, out var parsed))
            {
                Amount = parsed;
            }
        }
    }

    public ReinforcementTroopRule ToRule()
    {
        return new ReinforcementTroopRule
        {
            AccountName = AccountName,
            SourceVillageName = SourceVillageName,
            TroopType = TroopType,
            IsEnabled = IsEnabled,
            AmountMode = AmountMode,
            Amount = Amount,
        }.Normalize();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetValue<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string NormalizeAmountMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "all_available" => "all_available",
            "percent_20" => "percent_20",
            "percent_50" => "percent_50",
            "percent_90" => "percent_90",
            _ => "fixed",
        };
    }
}
