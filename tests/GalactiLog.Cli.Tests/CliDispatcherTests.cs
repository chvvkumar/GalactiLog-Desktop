using System;
using System.IO;
using Xunit;
using GalactiLog.Cli;

namespace GalactiLog.Cli.Tests;

public class CliDispatcherTests
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static readonly Func<IServiceProvider> Services = () => new NullServiceProvider();

    [Fact]
    public void TryRun_EmptyArgs_ReturnsFalseWithExitCodeZeroWithoutBuildingServices()
    {
        var servicesCalled = false;
        Func<IServiceProvider> services = () =>
        {
            servicesCalled = true;
            throw new InvalidOperationException("services() must not be invoked for empty args.");
        };

        var handled = CliDispatcher.TryRun(Array.Empty<string>(), services, out var exitCode);

        Assert.False(handled);
        Assert.Equal(0, exitCode);
        Assert.False(servicesCalled);
    }

    [Fact]
    public void TryRun_UnknownFirstArgument_PrintsUsageToStderrOnlyReturnsExitCodeTwoWithoutBuildingServices()
    {
        var servicesCalled = false;
        Func<IServiceProvider> services = () =>
        {
            servicesCalled = true;
            throw new InvalidOperationException("services() must not be invoked for an unknown verb.");
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);

        bool handled;
        int exitCode;
        try
        {
            handled = CliDispatcher.TryRun(new[] { "--help" }, services, out exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.False(servicesCalled);
        Assert.Empty(outWriter.ToString());
        Assert.Contains("Usage: galactilog", errorWriter.ToString());
    }

    // Spec 12.11 behaviour 9 (Phase 11 Task 4). The GUI's --minimized switch is not a verb, and in
    // isolation this dispatcher answers it the way it answers any unknown first token: usage on
    // stderr, exit 2, no host. That contract is deliberately unchanged. What changed is upstream:
    // Program.Main consumes the switch before this branch is reached, so a launch whose whole
    // command line is that switch never arrives here at all (spec 4.4 step 2). The literal is
    // written out because GalactiLog.Cli does not reference GalactiLog.App, where the one constant
    // that owns it lives; the census that keeps the spelling single is over src, not tests.
    [Fact]
    public void TryRun_TheMinimizedSwitch_IsNotAVerbAndBuildsNoServices()
    {
        var servicesCalled = false;
        Func<IServiceProvider> services = () =>
        {
            servicesCalled = true;
            throw new InvalidOperationException("services() must not be invoked for an unknown verb.");
        };

        var originalError = Console.Error;
        var errorWriter = new StringWriter();
        Console.SetError(errorWriter);

        bool handled;
        int exitCode;
        try
        {
            handled = CliDispatcher.TryRun(new[] { "--minimized" }, services, out exitCode);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.False(servicesCalled);
        Assert.Contains("Usage: galactilog", errorWriter.ToString());
    }

    [Theory]
    [InlineData("scan")]
    public void TryRun_EveryUnimplementedVerb_IsRoutedAfterServicesFactoryIsCalled(string verb)
    {
        var servicesCalled = false;
        Func<IServiceProvider> services = () =>
        {
            servicesCalled = true;
            return new NullServiceProvider();
        };

        var handled = CliDispatcher.TryRun(new[] { verb }, services, out var exitCode);

        Assert.True(handled);
        Assert.Equal(70, exitCode);
        Assert.True(servicesCalled);
    }

    // resolve is implemented as of Phase 3 Task 8: called with no name argument it hits its
    // own usage check (exit code 2), not the Phase 1 stub's 70 -- but unlike inspect/
    // dump-headers below, resolve still builds the host first (spec 4.4: "only verbs that
    // need the DB build the host" says nothing about argument count), so servicesCalled is
    // still true here. Full resolve behavior is covered by ResolveVerbTests.
    [Fact]
    public void TryRun_Resolve_MissingNameArgument_ReturnsUsageExitCodeAfterServicesFactoryIsCalled()
    {
        var servicesCalled = false;
        Func<IServiceProvider> services = () =>
        {
            servicesCalled = true;
            return new NullServiceProvider();
        };

        var handled = CliDispatcher.TryRun(new[] { "resolve" }, services, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.True(servicesCalled);
    }

    // inspect/dump-headers are implemented as of Phase 2: called with no file argument they
    // hit their own usage check (exit code 2), not the Phase 1 stub's 70. Full behavior is
    // covered by InspectAndDumpHeadersTests.
    [Theory]
    [InlineData("inspect")]
    [InlineData("dump-headers")]
    public void TryRun_InspectAndDumpHeaders_MissingFileArgument_ReturnsUsageExitCode(string verb)
    {
        var handled = CliDispatcher.TryRun(new[] { verb }, Services, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    // spec 15: only verbs that need the database build the host. inspect/dump-headers must
    // never call the factory at all -- a factory that throws if invoked must not throw here.
    [Theory]
    [InlineData("inspect")]
    [InlineData("dump-headers")]
    public void TryRun_InspectAndDumpHeaders_NeverInvokeServicesFactory(string verb)
    {
        Func<IServiceProvider> services = () =>
            throw new InvalidOperationException($"services() must not be invoked for {verb}.");

        var handled = CliDispatcher.TryRun(new[] { verb }, services, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }
}
