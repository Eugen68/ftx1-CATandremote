using Ftx1.Core;
using Xunit;
namespace Ftx1.Core.Tests;
public class ConfigTests
{
    [Theory][InlineData(9)][InlineData(601)] public void InvalidTxLimitsRejected(int value) => Assert.Throws<ArgumentException>(() => new StationConfig { MaxTxSeconds = value }.Validate());
    [Fact]
    public void AtomicSaveAndFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "ftx1-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new ConfigStore(Path.Combine(root, "primary"), Path.Combine(root, "backup"));
            store.Save(new() { Simulator = true, MaxTxSeconds = 123 }); Assert.Equal(123, store.Load().MaxTxSeconds); Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            File.WriteAllText(Path.Combine(root, "blocked"), "file"); var fallback = new ConfigStore(Path.Combine(root, "blocked"), Path.Combine(root, "backup"));
            fallback.Save(new() { MaxTxSeconds = 124 }); Assert.Equal(Path.Combine(root, "backup", "config.json"), fallback.ActivePath); Assert.Equal(124, fallback.Load().MaxTxSeconds);
        }
        finally { Directory.Delete(root, true); }
    }
}
