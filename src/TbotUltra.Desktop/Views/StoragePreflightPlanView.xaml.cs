using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class StoragePreflightPlanView : UserControl
{
    public StoragePreflightPlanView(
        string description,
        IReadOnlyList<StoragePreflightPlanStage> stages)
    {
        InitializeComponent();
        Description = description;
        Stages = stages;
        DataContext = this;
    }

    public string Description { get; }

    public IReadOnlyList<StoragePreflightPlanStage> Stages { get; }
}

public sealed record StoragePreflightPlanStage(
    string Badge,
    string Heading,
    string Requirement,
    IReadOnlyList<StoragePreflightPlanAction> Actions);

public sealed record StoragePreflightPlanAction(
    string Kind,
    string Label,
    string Building,
    string Details,
    string Capacity);
