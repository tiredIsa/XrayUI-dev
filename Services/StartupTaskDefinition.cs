using System;
using System.IO;
using System.Security;

namespace XrayUI.Services;

public static class StartupTaskDefinition
{
    public const string StartupMinimizedArgument = "--startup-minimized";
    public static string Build(string executablePath, string userSid, bool runElevated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var sid = SecurityElement.Escape(userSid);
        var exe = SecurityElement.Escape(executablePath);
        var workingDir = SecurityElement.Escape(Path.GetDirectoryName(executablePath) ?? "");
        var runLevel = runElevated ? "HighestAvailable" : "LeastPrivilege";
        // ExecutionTimeLimit=PT0S avoids Windows' 72h default killing the app
        // on long-running sessions. The other two Settings defaults (battery
        // behavior) would otherwise refuse to start / kill us on unplug.
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{sid}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>{runLevel}</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>{StartupMinimizedArgument}</Arguments>
      <WorkingDirectory>{workingDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }
}
