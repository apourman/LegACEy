using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class PostUiDrawHookTests
{
    [Fact]
    public void Signature_must_be_unique_and_in_an_executable_x86_PE_section()
    {
        var image = new byte[512];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        Put(0x3c, 64);
        Put(64, 0x4550);
        BitConverter.GetBytes((ushort)0x14c).CopyTo(image, 68);
        BitConverter.GetBytes((ushort)1).CopyTo(image, 70);
        Put(88 + 12, 0x1000);
        Put(88 + 16, 256);
        Put(88 + 20, 256);
        Put(88 + 36, 0x20000000);
        PostUiDrawHook.Signature.CopyTo(image, 280);
        Assert.Equal(0x1018, PostUiDrawHook.FindEndSceneRva(image));
        PostUiDrawHook.Signature.CopyTo(image, 320);
        Assert.Null(PostUiDrawHook.FindEndSceneRva(image));
        Array.Clear(image, 320, PostUiDrawHook.Signature.Length);
        Put(88 + 36, 0);
        Assert.Null(PostUiDrawHook.FindEndSceneRva(image));
        void Put(int offset, int value) => BitConverter.GetBytes(value).CopyTo(image, offset);
    }

    [Fact]
    public void Truncated_or_non_PE_files_are_rejected()
    {
        Assert.Null(PostUiDrawHook.FindEndSceneRva(Array.Empty<byte>()));
        var image = new byte[64];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        BitConverter.GetBytes(int.MaxValue).CopyTo(image, 0x3c);
        Assert.Null(PostUiDrawHook.FindEndSceneRva(image));
    }

    [Fact]
    public void Dispose_logs_nothing_when_no_hook_was_created()
    {
        var logged = new List<string>();
        var hook = new PostUiDrawHook(() => { }, _ => { }, logged.Add);
        hook.Dispose();
        hook.Dispose();
        Assert.Empty(logged);
    }
}
