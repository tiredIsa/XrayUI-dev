using XrayUI.Helpers;
namespace XrayUI.Services
{
    public sealed record ProgressDialogUpdate(LocalizedText Message, double? Percent = null);
}
