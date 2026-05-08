using TbotUltra.Desktop.Common;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>. Currently a placeholder that
/// inherits the INotifyPropertyChanged plumbing from <see cref="BaseViewModel"/>.
///
/// State is incrementally migrated from MainWindow fields into properties on
/// this view model as features get MVVM-ified. Until a property lives here,
/// the existing code-behind bindings (ElementName=RootWindow + ObservableCollection
/// fields) keep working unchanged.
/// </summary>
public sealed class MainViewModel : BaseViewModel
{
}
