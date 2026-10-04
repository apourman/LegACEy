using System.Runtime.InteropServices;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class PostUiDrawHookTests
{
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
        var vtable = Marshal.AllocHGlobal(43 * IntPtr.Size);
        var device = Marshal.AllocHGlobal(IntPtr.Size);
        var slot = IntPtr.Add(vtable, 42 * IntPtr.Size);
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
            Marshal.WriteIntPtr(device, vtable);
            Marshal.WriteIntPtr(slot, originalPointer);
            Assert.True(hook.Install(device));
            // Installation alone must not allow an invisible window to intercept input.
            Assert.False(hook.HasRun);
            var callbackPointer = Marshal.ReadIntPtr(slot);
            var callback = Marshal.GetDelegateForFunctionPointer<PostUiDrawHook.EndSceneDelegate>(callbackPointer);

            if (!drawFails) hook.Dispose();
            Assert.Equal(123, callback(device));
            Assert.Equal(drawFails, hook.HasRun);
            Assert.False(hook.IsInstalled);
            Assert.False(hook.Install(device));
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
            Marshal.FreeHGlobal(device);
            Marshal.FreeHGlobal(vtable);
            GC.KeepAlive(original);
        }
    }
}
