using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void DefaultActiveIndex_IsChat()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(1, vm.ActiveIndex);
    }

    [Fact]
    public void TryActivate_ValidIndex_SetsActiveIndex()
    {
        var vm = new MainWindowViewModel(7);
        Assert.True(vm.TryActivate(3));
        Assert.Equal(3, vm.ActiveIndex);
    }

    [Fact]
    public void TryActivate_InvalidIndex_ReturnsFalse()
    {
        var vm = new MainWindowViewModel(5);
        Assert.False(vm.TryActivate(-1));
        Assert.False(vm.TryActivate(5));
        Assert.False(vm.TryActivate(99));
        Assert.Equal(1, vm.ActiveIndex);
    }

    [Fact]
    public void SidebarCollapsed_Default_False()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.SidebarCollapsed);
    }

    [Fact]
    public void ToggleSidebar_TogglesState()
    {
        var vm = new MainWindowViewModel();
        vm.ToggleSidebar();
        Assert.True(vm.SidebarCollapsed);
        vm.ToggleSidebar();
        Assert.False(vm.SidebarCollapsed);
    }

    [Fact]
    public void RefreshStatus_SetsStatusRight()
    {
        var vm = new MainWindowViewModel();
        vm.RefreshStatus();
        Assert.StartsWith("CPU:", vm.StatusRight);
        Assert.Contains("MEM:", vm.StatusRight);
    }

    [Fact]
    public void PropertyChanged_Fires_OnActiveIndexChange()
    {
        var vm = new MainWindowViewModel(5);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.TryActivate(2);

        Assert.Contains(nameof(vm.ActiveIndex), changed);
    }

    [Fact]
    public void PropertyChanged_Fires_OnSidebarCollapsedChange()
    {
        var vm = new MainWindowViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ToggleSidebar();

        Assert.Contains(nameof(vm.SidebarCollapsed), changed);
    }

    [Fact]
    public void PropertyChanged_Fires_OnStatusRightChange()
    {
        var vm = new MainWindowViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.RefreshStatus();

        Assert.Contains(nameof(vm.StatusRight), changed);
    }

    [Fact]
    public void ViewCount_MatchesConstructor()
    {
        var vm = new MainWindowViewModel(10);
        Assert.Equal(10, vm.ViewCount);
    }

    [Fact]
    public void TryActivate_DoesNotChange_OnInvalidIndex()
    {
        var vm = new MainWindowViewModel(5);
        vm.TryActivate(3);
        Assert.Equal(3, vm.ActiveIndex);

        vm.TryActivate(-1);
        Assert.Equal(3, vm.ActiveIndex);
    }
}
