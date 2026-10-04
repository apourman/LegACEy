using System.Runtime.InteropServices;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class PostUiDrawHookTests
{
    [Fact]
    public void Decal_forwarding_slot_draws_before_EndScene_and_restores_on_unload()
    {
        var calls = new List<string>();
        var device = new IntPtr(1234);
        PostUiDrawHook.EndSceneDelegate original = actual =>
        {
            Assert.Equal(device, actual);
            calls.Add("EndScene");
            return 123;
        };
        var originalPointer = Marshal.GetFunctionPointerForDelegate(original);
        var slot = Marshal.AllocHGlobal(IntPtr.Size);
        using var hook = new PostUiDrawHook(() => calls.Add("windows"), _ => Assert.Fail("Draw failed"),
            (address, value) => { Marshal.WriteIntPtr(address, value); return true; });
        try
        {
            Marshal.WriteIntPtr(slot, originalPointer);
            Assert.True(hook.InstallForwardingSlot(slot));
            Assert.False(hook.HasRun);
            // Decal calls this exported pointer, independently of the device's current vtable.
            var callback = Marshal.GetDelegateForFunctionPointer<PostUiDrawHook.EndSceneDelegate>(Marshal.ReadIntPtr(slot));
            Assert.Equal(123, callback(device));
            Assert.True(hook.HasRun);
            Assert.Equal(new[] { "windows", "EndScene" }, calls);
            hook.Dispose();
            Assert.Equal(originalPointer, Marshal.ReadIntPtr(slot));
        }
        finally
        {
            hook.Dispose();
            Marshal.FreeHGlobal(slot);
            GC.KeepAlive(original);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Failed_removal_keeps_forwarding_and_can_be_retried(bool drawFails, bool removalThrows)
    {
        var calls = 0;
        var failures = 0;
        PostUiDrawHook.EndSceneDelegate original = _ => { calls++; return 123; };
        var originalPointer = Marshal.GetFunctionPointerForDelegate(original);
        var device = new IntPtr(1234);
        var slot = Marshal.AllocHGlobal(IntPtr.Size);
        var allowRemoval = false;
        using var hook = new PostUiDrawHook(
            () => { if (drawFails) throw new InvalidOperationException("render failure"); },
            _ => { failures++; throw new InvalidOperationException("reporting failure"); },
            (address, value) =>
            {
                if (value == originalPointer && !allowRemoval)
                {
                    if (removalThrows) throw new InvalidOperationException("removal failure");
                    return false;
                }
                Marshal.WriteIntPtr(address, value);
                return true;
            });
        try
        {
            Marshal.WriteIntPtr(slot, originalPointer);
            Assert.True(hook.InstallForwardingSlot(slot));
            // Installation alone must not allow an invisible window to intercept input.
            Assert.False(hook.HasRun);
            var callbackPointer = Marshal.ReadIntPtr(slot);
            var callback = Marshal.GetDelegateForFunctionPointer<PostUiDrawHook.EndSceneDelegate>(callbackPointer);

            if (!drawFails) hook.Dispose();
            Assert.Equal(123, callback(device));
            Assert.Equal(drawFails, hook.HasRun);
            Assert.False(hook.IsInstalled);
            Assert.False(hook.InstallForwardingSlot(slot));
            Assert.Equal(callbackPointer, Marshal.ReadIntPtr(slot));
            Assert.Equal(123, callback(device));
            Assert.Equal(2, calls);
            Assert.Equal(drawFails ? 1 : 0, failures);

            allowRemoval = true;
            Assert.Equal(123, callback(device));
            Assert.Equal(originalPointer, Marshal.ReadIntPtr(slot));
            Assert.Equal(3, calls);
        }
        finally
        {
            allowRemoval = true;
            hook.Dispose();
            Marshal.FreeHGlobal(slot);
            GC.KeepAlive(original);
        }
    }
}
