using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

public class KeyboardTests
{
    [Fact]
    public void The_hook_installs_on_its_own_thread_and_comes_off_cleanly()
    {
        // Version 1 hooked the keyboard on the UI thread; Windows silently drops a low-level hook whose
        // thread is too busy to answer, so version 2 gives it a thread and message loop of its own.
        using var k = new KeyboardDebouncer();
        k.Start();
        Assert.True(k.Running);
        k.Dispose();
        SpinWait.SpinUntil(() => !k.Running, TimeSpan.FromSeconds(2));
        Assert.False(k.Running);
    }

    [Fact]
    public void Starting_twice_is_harmless()
    {
        using var k = new KeyboardDebouncer();
        k.Start();
        k.Start();
        Assert.True(k.Running);
    }
}
