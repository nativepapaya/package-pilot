using System.Xml.Linq;

namespace PackagePilot.Tests.Build;

public sealed class UiPolishAcceptanceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void InstalledToolbarIsCompactAndKeepsAdvancedVisibilityInItsFilterMenu()
    {
        var document = LoadXaml("Views", "InstalledPage.xaml");
        var search = FindNamed(document, "AutoSuggestBox", "InstalledSearchBox");
        var visibilityToggle = FindNamed(
            document,
            "ToggleMenuFlyoutItem",
            "ShowWindowsManagedToggle");
        var installedRow = document
            .Descendants()
            .Single(element => element.Name.LocalName == "PackageRow");

        Assert.Equal("360", (string?)search.Attribute("MaxWidth"));
        Assert.Equal("Center", (string?)search.Attribute("VerticalAlignment"));
        Assert.Contains(
            visibilityToggle.Ancestors(),
            ancestor => ancestor.Name.LocalName == "DropDownButton.Flyout");
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "ToggleButton"
                && string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    "ShowWindowsManagedToggle",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{x:Bind ShowInstalledRowState, Mode=OneWay}",
            (string?)installedRow.Attribute("ShowState"));
    }

    [Fact]
    public void UpdatesNavigationStartsHiddenAndNeverUsesDotMode()
    {
        var document = LoadXaml("MainPage.xaml");
        var badge = FindNamed(document, "InfoBadge", "UpdatesBadge");
        var mainPageCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "PackagePilot.App",
            "MainPage.xaml.cs"));

        Assert.Equal("Collapsed", (string?)badge.Attribute("Visibility"));
        Assert.Equal("0", (string?)badge.Attribute("Value"));
        Assert.DoesNotContain("UpdatesBadge.Value = -1", mainPageCode, StringComparison.Ordinal);
        Assert.Contains("UpdateNavigationBadgeProjector.Create", mainPageCode, StringComparison.Ordinal);
    }

    private static XElement FindNamed(XDocument document, string elementName, string name) =>
        document
            .Descendants()
            .Single(element => element.Name.LocalName == elementName
                && string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    name,
                    StringComparison.Ordinal));

    private static XDocument LoadXaml(params string[] relativePath) =>
        XDocument.Load(Path.Combine(
            new[] { FindRepositoryRoot(), "src", "PackagePilot.App" }
                .Concat(relativePath)
                .ToArray()));

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(startingPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PackagePilot.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Package Pilot repository root.");
    }
}
