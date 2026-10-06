using System.Runtime.InteropServices;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.Tests;

public class TrayIconServiceTests
{
    private const uint Id = 0x5852;

    [Fact]
    public void FailedLogonRegistrationCanBeRetried()
    {
        var shell = new FakeShell { Available = false };
        using var tray = Create(shell);
        Assert.False(tray.EnsureRegistered());
        Assert.False(tray.EnsureRegistered());
        Assert.False(shell.Present);

        shell.Available = true;
        Assert.True(tray.EnsureRegistered());
        Assert.True(shell.Present);
        Assert.Equal(1, shell.SuccessfulAdds);
        Assert.Equal((uint)4, shell.Version);
    }

    [Fact]
    public void MaintenanceDoesNotAddDuplicatesAndDetectsLostIconWithoutBroadcast()
    {
        var shell = new FakeShell();
        using var tray = Create(shell);
        Assert.True(tray.EnsureRegistered());
        for (var i = 0; i < 5; i++) Assert.True(tray.EnsureRegistered());
        Assert.Equal(1, shell.SuccessfulAdds);

        shell.Present = false;
        Assert.True(tray.EnsureRegistered());
        Assert.Equal(2, shell.SuccessfulAdds);
    }

    [Fact]
    public void ExplorerRestartRestoresLatestIconAndTooltipWhenShellReturns()
    {
        var shell = new FakeShell();
        using var tray = Create(shell);
        tray.EnsureRegistered();
        shell.Present = false;
        shell.Available = false;
        Assert.False(tray.OnTaskbarCreated());
        tray.SetIcon("running.ico");
        tray.SetTooltip("Connected: selected server");
        Assert.False(shell.Present);

        shell.Available = true;
        Assert.True(tray.EnsureRegistered());
        Assert.Equal("Connected: selected server", shell.Tooltip);
        Assert.Equal(new IntPtr(2), shell.Icon);
        Assert.Equal((uint)0x87, shell.Flags);
        Assert.Equal(new[] { new IntPtr(1) }, shell.Destroyed);
    }

    [Fact]
    public void DuplicateAddFallsBackToUpdatingExistingIcon()
    {
        var shell = new FakeShell { Present = true };
        using var tray = Create(shell);
        Assert.True(tray.EnsureRegistered());
        Assert.Equal(0, shell.SuccessfulAdds);
        Assert.Equal("Idle", shell.Tooltip);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CallbacksMatchVersionAndRejectOtherIcons(bool version4)
    {
        var shell = new FakeShell { AcceptVersion4 = version4 };
        using var tray = Create(shell);
        tray.EnsureRegistered();
        var select = version4 ? 0x0400u : 0x0202u;
        var menu = version4 ? 0x007bu : 0x0205u;
        nint Callback(uint id, uint message) => (nint)(version4 ? (id << 16) | message : message);
        Assert.True(tray.TryGetAction(Id, Callback(Id, select), out var context));
        Assert.False(context);
        Assert.True(tray.TryGetAction(Id, Callback(Id, menu), out context));
        Assert.True(context);
        Assert.False(tray.TryGetAction(Id + 1, Callback(Id + 1, select), out _));
        Assert.False(tray.TryGetAction(Id, Callback(Id, 0x0200), out _));
    }

    [Fact]
    public void TooltipUsesShellLimitAndNullTerminator()
    {
        var shell = new FakeShell();
        using var tray = Create(shell);
        tray.SetTooltip(new string('Ж', 200));
        Assert.Equal(new string('Ж', 127), shell.Tooltip);
        tray.SetTooltip("Short");
        Assert.Equal("Short", shell.Tooltip);
    }

    [Fact]
    public void DisposeDeletesIconAndReleasesEachHandleOnceWithoutReregistering()
    {
        var shell = new FakeShell();
        var tray = Create(shell);
        tray.EnsureRegistered();
        tray.SetIcon("running.ico");
        tray.SetIcon("running.ico");
        tray.Dispose();
        tray.Dispose();
        tray.SetIcon("idle.ico");
        tray.SetTooltip("After exit");
        Assert.False(tray.EnsureRegistered());
        Assert.False(tray.OnTaskbarCreated());
        Assert.False(tray.TryGetAction(Id, (nint)((Id << 16) | 0x0400), out _));
        Assert.False(shell.Present);
        Assert.Equal(1, shell.Deletes);
        Assert.Equal(new[] { new IntPtr(1), new IntPtr(2) }, shell.Destroyed);
    }

    [Fact]
    public void ShellStructuresHaveNativeWindowsLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 976 : 956, Marshal.SizeOf<TrayIconInterop.IconData>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<TrayIconInterop.IconIdentifier>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 24, Marshal.OffsetOf<TrayIconInterop.IconData>(nameof(TrayIconInterop.IconData.Tip)).ToInt32());
    }

    private static TrayIconService Create(FakeShell shell) => new(new IntPtr(123), Id, "idle.ico", "Idle", shell);

    private sealed class FakeShell : ITrayIconApi
    {
        public bool Available = true, Present, AcceptVersion4 = true;
        public int SuccessfulAdds, Deletes;
        public uint Version, Flags;
        public IntPtr Icon;
        public string Tooltip = "";
        public List<IntPtr> Destroyed { get; } = new();
        private int _loadedIcons;

        public IntPtr LoadIcon(string path) => new(++_loadedIcons);
        public void DestroyIcon(IntPtr icon) => Destroyed.Add(icon);

        public unsafe bool Notify(uint command, ref TrayIconInterop.IconData data)
        {
            Assert.Equal(new IntPtr(123), data.Window);
            Assert.Equal(Id, data.Id);
            if (command == TrayIconInterop.Delete)
            {
                Deletes++;
                Present = false;
                return true;
            }
            if (!Available) return false;
            if (command == TrayIconInterop.SetVersion)
            {
                Version = data.Version;
                return Present && AcceptVersion4;
            }
            if (command == TrayIconInterop.Add)
            {
                if (Present) return false;
                Present = true;
                SuccessfulAdds++;
            }
            if (!Present) return false;
            Icon = data.Icon;
            Flags = data.Flags;
            fixed (char* tip = data.Tip) Tooltip = new string(tip);
            return true;
        }
    }
}
