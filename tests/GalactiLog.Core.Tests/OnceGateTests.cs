using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using GalactiLog.Core;
using Xunit;

namespace GalactiLog.Core.Tests;

public class OnceGateTests
{
    [Fact]
    public void TryFire_FirstCall_ReturnsTrue()
    {
        var gate = new OnceGate();
        Assert.True(gate.TryFire());
    }

    [Fact]
    public void TryFire_SecondCall_ReturnsFalse()
    {
        var gate = new OnceGate();
        gate.TryFire();
        Assert.False(gate.TryFire());
    }

    [Fact]
    public async Task TryFire_ConcurrentCallers_ExactlyOneReturnsTrue()
    {
        var gate = new OnceGate();
        var results = new ConcurrentBag<bool>();

        var tasks = new Task[50];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => results.Add(gate.TryFire()));
        }
        await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }
}
