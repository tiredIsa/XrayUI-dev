using System.Xml.Linq;
using XrayUI.Services;

namespace XrayUI.Tests;

public class StartupTaskDefinitionTests
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Theory]
    [InlineData(false, "LeastPrivilege")]
    [InlineData(true, "HighestAvailable")]
    public void SelectsPrivilegeLevelAndKeepsInteractiveMinimizedLogon(bool elevated, string expectedLevel)
    {
        var path = Path.Combine(Path.GetTempPath(), "XrayUI", "xray.exe");
        var task = XDocument.Parse(StartupTaskDefinition.Build(path, "S-1-5-21-123-456-789-1001", elevated));
        string Value(string name) => task.Descendants(TaskNamespace + name).Single().Value;

        Assert.Equal(expectedLevel, Value("RunLevel"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal(StartupTaskDefinition.StartupMinimizedArgument, Value("Arguments"));
        Assert.Equal("PT5S", Value("Delay"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.All(task.Descendants(TaskNamespace + "UserId"), element =>
            Assert.Equal("S-1-5-21-123-456-789-1001", element.Value));
    }

    [Fact]
    public void PreservesPathsWithSpacesAndXmlCharactersAsOneExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), "Xray & tools", "xray.exe");
        var task = XDocument.Parse(StartupTaskDefinition.Build(path, "S-1-5-21-1001", true));
        Assert.Equal(path, task.Descendants(TaskNamespace + "Command").Single().Value);
        Assert.Equal(Path.GetDirectoryName(path), task.Descendants(TaskNamespace + "WorkingDirectory").Single().Value);
        Assert.Single(task.Descendants(TaskNamespace + "Exec"));
    }

    [Fact]
    public void RejectsMissingExecutableOrAccount()
    {
        Assert.Throws<ArgumentException>(() => StartupTaskDefinition.Build("", "S-1-5-21-1001", true));
        Assert.Throws<ArgumentException>(() => StartupTaskDefinition.Build("xray.exe", "", true));
    }
}
