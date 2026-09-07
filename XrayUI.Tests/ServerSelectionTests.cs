using XrayUI.Helpers;
namespace XrayUI.Tests;
public class ServerSelectionTests
{
    [Fact] public void RangeAndControlSelectionUseLogicalOrder()
    {
        var state = new ServerSelectionState();
        string[] order = ["a", "b", "c", "d"];
        state.Choose("b", order, false, false);
        state.Choose("d", order, false, true);
        Assert.Equal(new[] { "b", "c", "d" }, state.Selected.Order());
        state.Choose("c", order, true, false);
        Assert.Equal(new[] { "b", "d" }, state.Selected.Order());
    }
    [Fact] public void HidingGroupPreservesCurrentDetailsButRemovesHiddenMassSelection()
    {
        var state = new ServerSelectionState(); state.Replace("a", ["a", "b"]);
        state.Hide(["a"]);
        Assert.Equal("a", state.CurrentId); Assert.Equal(new[] { "b" }, state.Selected);
    }
    [Fact] public void HiddenRangeAnchorDoesNotSelectUnrelatedItems()
    {
        var state = new ServerSelectionState(); state.Replace("gone", ["gone"]);
        state.Choose("b", ["a", "b", "c"], false, true);
        Assert.Equal(new[] { "b" }, state.Selected);
    }
}
