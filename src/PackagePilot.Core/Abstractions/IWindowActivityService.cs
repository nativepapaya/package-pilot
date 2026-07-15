using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IWindowActivityService
{
    event EventHandler<WindowActivityChangedEventArgs>? ActivityChanged;

    WindowActivityState Current { get; }
}
