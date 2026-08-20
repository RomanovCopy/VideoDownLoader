using System.Xml.Linq;

namespace VideoDownLoader.Tests;

public sealed class ThemeContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DarkTheme_DeclaresReadableSemanticForegrounds()
    {
        var document = LoadTheme();

        Assert.Equal("#F9FAFB", GetBrushColor(document, "TextBrush"));
        Assert.Equal("#AAB4C3", GetBrushColor(document, "MutedBrush"));
        Assert.Equal("#7D899B", GetBrushColor(document, "DisabledTextBrush"));
        Assert.Equal("#082F49", GetBrushColor(document, "AccentTextBrush"));
        Assert.Equal("#293548", GetBrushColor(document, "BorderBrush"));
        AssertStyleSetter(document, "TextBlock", "Foreground", "{StaticResource TextBrush}");
    }

    [Fact]
    public void MainWindow_ExplicitlyUsesDarkRootSurface()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
        var window = Assert.Single(document.Elements(Presentation + "Window"));

        Assert.Equal("{StaticResource BackgroundBrush}", (string?)window.Attribute("Background"));
        Assert.Equal("{StaticResource TextBrush}", (string?)window.Attribute("Foreground"));
    }

    [Theory]
    [InlineData("ClearUrlButton", "ClearUrlButton_Click")]
    [InlineData("ClearThumbnailButton", "ClearThumbnailButton_Click")]
    [InlineData("ClearQueueButton", "ClearQueueButton_Click")]
    public void VideoDownload_ClearActions_AreWired(string buttonName, string actionName)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
        var button = document.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == buttonName);
        var action = button.Attributes()
            .Single(attribute => attribute.Name.LocalName == "MainWindowBehavior.Action");

        Assert.Equal(actionName, action.Value);
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("CheckBox")]
    [InlineData("ComboBox")]
    [InlineData("ComboBoxItem")]
    [InlineData("ListViewItem")]
    [InlineData("TabControl")]
    [InlineData("TabItem")]
    public void DarkTheme_SystemColorSensitiveControl_HasExplicitTemplate(string targetType)
    {
        var style = GetStyle(LoadTheme(), targetType);

        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Template" && setter.Descendants(Presentation + "ControlTemplate").Any());
    }

    private static XDocument LoadTheme() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));

    private static string? GetBrushColor(XDocument document, string key) =>
        document.Descendants(Presentation + "SolidColorBrush")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key)
            .Attribute("Color")?.Value;

    private static void AssertStyleSetter(
        XDocument document,
        string targetType,
        string property,
        string expectedValue)
    {
        var setter = GetStyle(document, targetType)
            .Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property);
        Assert.Equal(expectedValue, (string?)setter.Attribute("Value"));
    }

    private static XElement GetStyle(XDocument document, string targetType) =>
        document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute("TargetType") == targetType);
}
