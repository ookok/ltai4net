using LTAI.Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;

namespace LTAI.TUI.Tests;

/// <summary>Terminal.Gui v2 integration tests using InputInjector + VirtualTimeProvider.
/// Verifies keyboard shortcuts don't crash and basic window behavior.</summary>
public sealed class TuiKeyboardIntegrationTests : IDisposable
{
    private IApplication _app = null!;
    private MainWindow _window = null!;

    private void Init()
    {
        var time = new VirtualTimeProvider();
        _app = Application.Create(time);
        _app.Init(DriverRegistry.Names.ANSI);

        var dir = Path.Combine(Path.GetTempPath(), "ltai-tui-int", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sessionMgr = new SessionManager(dir, new LTAI.Core.Session.JsonSessionSerializer());
        var sp = new ServiceCollection().BuildServiceProvider();
        var logger = NullLogger<MainWindow>.Instance;

        _window = new MainWindow(_app, null!, sessionMgr, logger, "test-model", sp);
        
    }

    [Fact]
    public void ApplicationLifecycle_DoesNotThrow()
    {
        Init();
        Assert.NotNull(_app);
        Assert.NotNull(_window);
        Assert.Equal("LTAI", _window.Title);
    }

    [Fact]
    public void CtrlN_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.N.WithCtrl);
        
    }

    [Fact]
    public void CtrlL_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.L.WithCtrl);
        
    }

    [Fact]
    public void CtrlP_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.P.WithCtrl);
        
    }

    [Fact]
    public void CtrlQ_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.Q.WithCtrl);
        
    }

    [Fact]
    public void CtrlR_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.R.WithCtrl);
        
    }

    [Fact]
    public void CtrlT_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.T.WithCtrl);
        
    }

    [Fact]
    public void Tab_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.Tab);
        
    }

    [Fact]
    public void CtrlUpDown_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.CursorUp.WithCtrl);
        
        _app.InjectKey(Key.CursorDown.WithCtrl);
        
    }

    [Fact]
    public void Enter_WithSlashCommand_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.Enter);
        
    }

    [Fact]
    public void MultipleShortcuts_Sequential_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.P.WithCtrl);
        
        _app.InjectKey(Key.Esc);
        
        _app.InjectKey(Key.N.WithCtrl);
        
        _app.InjectKey(Key.L.WithCtrl);
        
    }

    [Fact]
    public void Backspace_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.Backspace);
        
    }

    [Fact]
    public void ShiftEnter_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.Enter.WithShift);
        
    }

    [Fact]
    public void CtrlC_DoesNotThrow()
    {
        Init();
        _app.InjectKey(Key.C.WithCtrl);
        
    }

    public void Dispose()
    {
        _window?.Dispose();
        _app?.Dispose();
    }
}
