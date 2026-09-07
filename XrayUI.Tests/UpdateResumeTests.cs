using XrayUI.Helpers;

namespace XrayUI.Tests;

public class UpdateResumeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTripsExactServerAndMode(bool tun)
    {
        var resume = new UpdateResume("server / \"Москва\" & other", tun);
        Assert.Equal(resume, UpdateResume.Parse(["app.exe", resume.ToArgument()]));
    }

    [Theory]
    [InlineData("--startup-minimized")]
    [InlineData("--resume-update-tun=")]
    [InlineData("--resume-update-proxy=invalid")]
    [InlineData("--resume-update-tun=/w==")]
    public void IgnoresAbsentOrMalformedHandoff(string argument)
    {
        Assert.Null(UpdateResume.Parse([argument]));
    }

    [Fact]
    public void RejectsAmbiguousHandoff()
    {
        Assert.Null(UpdateResume.Parse([
            new UpdateResume("a", true).ToArgument(),
            new UpdateResume("b", false).ToArgument()]));
    }
}
