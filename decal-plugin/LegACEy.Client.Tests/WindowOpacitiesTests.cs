using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class WindowOpacitiesTests
{
    [Fact]
    public void A_window_draws_at_its_own_opacity_or_else_all_windows_kept_per_character()
    {
        var store = new MemoryWindowPositionStore();
        var opacities = new WindowOpacities(store);
        Assert.Equal(100, opacities.Of("server", "Alice", "dereth-radar"));

        opacities.SetAll("server", "Alice", 70);
        opacities.Set("server", "Alice", "dereth-radar", 42);

        Assert.Equal(40, opacities.Of("server", "Alice", "dereth-radar"));
        // A plugin window with no value of its own follows All windows.
        Assert.Equal(70, opacities.Of("server", "Alice", "Vault/vault"));
        Assert.Equal(100, opacities.Of("server", "Bob", "dereth-radar"));
        // Never so faint the window is lost; and after a restart the same store still has Alice's values.
        opacities.Set("server", "Alice", "Vault/vault", 0);
        var restarted = new WindowOpacities(store);
        Assert.Equal(WindowOpacities.Faintest, restarted.Of("server", "Alice", "Vault/vault"));
        Assert.Equal(40, restarted.Of("server", "Alice", "dereth-radar"));
    }
}
