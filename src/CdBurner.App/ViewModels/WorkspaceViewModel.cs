using CdBurner.App.Mvvm;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Represents a workspace module displayed in the central ContentControl of the main window.
/// </summary>
public class WorkspaceViewModel : ObservableObject
{
    private NavigationTab _tab;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _badgeText = string.Empty;

    /// <summary>
    /// Associated navigation tab identifier.
    /// </summary>
    public NavigationTab Tab
    {
        get => _tab;
        set => SetProperty(ref _tab, value);
    }

    /// <summary>
    /// User-facing title of the workspace.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// Description explaining the purpose and capabilities of this workspace.
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// Secondary badge or status descriptor.
    /// </summary>
    public string BadgeText
    {
        get => _badgeText;
        set => SetProperty(ref _badgeText, value);
    }

    /// <summary>
    /// Initializes a new workspace view model.
    /// </summary>
    public WorkspaceViewModel(NavigationTab tab, string title, string description, string badgeText = "")
    {
        Tab = tab;
        Title = title;
        Description = description;
        BadgeText = badgeText;
    }
}
