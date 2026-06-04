using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop.Tests;

public sealed class DashboardViewModelTests
{
    private static DashboardViewModel CreateVm() => new("test-mode", "simplified", "safe", 16384);

    [Fact]
    public void Refresh_SetsSysInfo()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.Contains("模式:", vm.SysInfo);
        Assert.Contains("PID:", vm.SysInfo);
        Assert.Contains("运行:", vm.SysInfo);
    }

    [Fact]
    public void Refresh_SetsHealthInfo()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.Contains("GC 内存:", vm.HealthInfo);
        Assert.Contains("线程:", vm.HealthInfo);
        Assert.Contains("NET:", vm.HealthInfo);
    }

    [Fact]
    public void Refresh_SetsSessionInfo()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.Contains("模型:", vm.SessionInfo);
        Assert.Contains("Token:", vm.SessionInfo);
        Assert.Contains("请求:", vm.SessionInfo);
        Assert.Contains("费用:", vm.SessionInfo);
    }

    [Fact]
    public void Refresh_SetsContextRatio()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.InRange(vm.ContextRatio, 0, 100);
    }

    [Fact]
    public void Refresh_SetsContextLabel()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.Contains("上下文容量:", vm.ContextLabel);
    }

    [Fact]
    public void Refresh_SetsCacheHitRate()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.InRange(vm.CacheHitRate, 0, 100);
    }

    [Fact]
    public void Refresh_SetsCacheLabel()
    {
        var vm = CreateVm();
        vm.Refresh();

        Assert.Contains("缓存命中:", vm.CacheLabel);
    }

    [Fact]
    public void PropertyChanged_FiresOnRefresh()
    {
        var vm = CreateVm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Refresh();

        Assert.Contains(nameof(vm.SysInfo), changed);
        Assert.Contains(nameof(vm.HealthInfo), changed);
        Assert.Contains(nameof(vm.SessionInfo), changed);
        Assert.Contains(nameof(vm.ContextLabel), changed);
        Assert.Contains(nameof(vm.CacheLabel), changed);
    }

    [Fact]
    public void SetDevUiStatus_SetsProperties()
    {
        var vm = CreateVm();
        vm.SetDevUiStatus("running", true);

        Assert.Equal("running", vm.DevUiStatus);
        Assert.True(vm.DevUiStatusVisible);
    }

    [Fact]
    public void DefaultValues_AreEmpty()
    {
        var vm = CreateVm();

        Assert.Equal("", vm.SysInfo);
        Assert.Equal("", vm.HealthInfo);
        Assert.Equal("", vm.SessionInfo);
        Assert.Equal("", vm.DevUiStatus);
        Assert.False(vm.DevUiStatusVisible);
    }
}
