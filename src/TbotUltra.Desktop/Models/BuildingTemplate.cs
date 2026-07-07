using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TbotUltra.Desktop.Models;

public enum BuildingTemplateRowKind
{
    Building = 0,
    AllResources = 1,
}

public sealed class BuildingTemplate : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = string.Empty;
    private string _createdByTribe = "Unknown";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string CreatedByTribe
    {
        get => _createdByTribe;
        set => SetProperty(ref _createdByTribe, value);
    }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<BuildingTemplateRow> Rows { get; set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class BuildingTemplateRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BuildingTemplateRowKind Kind { get; set; } = BuildingTemplateRowKind.Building;
    public int? Gid { get; set; }
    public string BuildingName { get; set; } = string.Empty;
    public int? PreferredSlotId { get; set; }
    public int TargetLevel { get; set; } = 1;
    public string ResourceStrategy { get; set; } = "lowest";
}
