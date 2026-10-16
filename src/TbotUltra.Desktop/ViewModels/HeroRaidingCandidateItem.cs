using TbotUltra.Desktop.Common;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.ViewModels;

public sealed class HeroRaidingCandidateItem(HeroRaidingScanCandidate candidate, int minimumHpPercent) : BaseViewModel
{
    private bool _isExcluded = candidate.IsExcluded;

    public int X => candidate.X;
    public int Y => candidate.Y;
    public string Coordinates => $"{X}|{Y}";
    public string MapPath => $"/karte.php?x={X}&y={Y}";
    public string OasisType => candidate.OasisType;
    public string Animals => candidate.Animals;
    public string PredictedHp => candidate.PredictedHpAfterRaid is int hp ? $"{hp}%" : "Unknown";
    public string EstimatedBounty => candidate.EstimatedBountyPerResource is int bounty ? bounty.ToString() : "Unknown";
    public string Distance => candidate.DistanceFields.ToString("0.0");
    public bool IsSafe => candidate.IsSafe && !IsExcluded;
    public bool IsUnsafeEstimate => !candidate.IsSafe;
    public string Safety => IsExcluded ? "Excluded"
        : candidate.IsSafe && candidate.PredictedHpAfterRaid is int hp && hp < minimumHpPercent + 10
            ? "Borderline"
        : candidate.IsSafe ? "Safe"
        : candidate.PredictedHpAfterRaid.HasValue ? "Unsafe" : "Unknown";
    public DateTimeOffset ObservedAtUtc => candidate.ObservedAtUtc;

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (SetProperty(ref _isExcluded, value))
            {
                OnPropertyChanged(nameof(IsSafe));
                OnPropertyChanged(nameof(Safety));
            }
        }
    }
}
