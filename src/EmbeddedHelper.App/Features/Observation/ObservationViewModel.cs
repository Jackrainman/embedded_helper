using System.Collections.ObjectModel;

namespace EmbeddedHelper.App;

public sealed class ObservationViewModel
{
    public ObservableCollection<VariableRow> Variables { get; } = new();
}
