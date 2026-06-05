using System.Reflection;

namespace LTAI.Desktop.Tests;

/// <summary>
/// Tests for ConfigDialog.
/// ConfigDialog extends Window, which requires full Avalonia compositor thread.
/// Only static/shared state tests are viable without full windowing setup.
/// </summary>
public sealed class ConfigDialogTests
{
    [Fact]
    public void SharedHttpClient_Has10sTimeout()
    {
        var field = typeof(ConfigDialog).GetField("_sharedHttp",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var client = (System.Net.Http.HttpClient)field.GetValue(null)!;
        Assert.NotNull(client);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }
}
